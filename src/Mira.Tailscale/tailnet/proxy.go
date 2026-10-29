package tailnet

import (
	"bufio"
	"context"
	"crypto/subtle"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/netip"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"tailscale.com/net/proxymux"
)

// DialFunc connects over the tailnet, as Node.Dial does.
type DialFunc func(ctx context.Context, network, address string) (net.Conn, error)

// ProxyHeader carries the node's Code in the HTTP proxy's 502 answers (the same header as the iPhone's relay).
const ProxyHeader = "X-Mira-Tailscale"

const (
	// proxyDialTimeout is how long a connection through the proxy waits for the node and the device.
	proxyDialTimeout = 20 * time.Second
	// handshakeTimeout bounds a SOCKS5 greeting: a client that says nothing loses its connection.
	handshakeTimeout = 10 * time.Second
)

// Proxy serves the node to an app's own network stacks on a loopback port, behind a user name and a password: SOCKS5
// (CONNECT only: .NET's HttpClient) and HTTP proxying (CONNECT and absolute-form requests: mpv and its FFmpeg), told
// apart by their first byte. It reaches devices of the tailnet only, and never logs a request's path (Jellyfin's
// addresses can carry an access token).
type Proxy struct {
	dial      DialFunc
	logf      func(string, ...any)
	auth      string // the expected Proxy-Authorization
	user      []byte
	password  []byte
	ln        net.Listener
	server    *http.Server
	transport *http.Transport
	forward   *httputil.ReverseProxy

	mu     sync.Mutex
	conns  map[net.Conn]struct{} // SOCKS5 connections and HTTP tunnels, which the HTTP server no longer tracks
	closed bool
}

// ServeProxy serves the node on address, a loopback one (« 127.0.0.1:0 » takes a free port).
func (n *Node) ServeProxy(address, user, password string) (*Proxy, error) {
	return ServeProxy(address, user, password, n.Dial, n.logf)
}

// ServeProxy serves dial on address, a loopback one.
func ServeProxy(address, user, password string, dial DialFunc, logf func(string, ...any)) (*Proxy, error) {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	if ip, err := netip.ParseAddr(host); err != nil || !ip.IsLoopback() {
		return nil, fmt.Errorf("tailnet: the proxy listens on a loopback address only, not %q", host)
	}
	if user == "" || password == "" || strings.Contains(user, ":") || len(user) > 255 || len(password) > 255 {
		return nil, errors.New("tailnet: the proxy needs a user name (no colon) and a password, of 255 bytes at most")
	}
	if logf == nil {
		logf = func(string, ...any) {}
	}
	ln, err := net.Listen("tcp", address)
	if err != nil {
		return nil, err
	}
	p := &Proxy{
		dial:     dial,
		logf:     logf,
		auth:     "Basic " + base64.StdEncoding.EncodeToString([]byte(user+":"+password)),
		user:     []byte(user),
		password: []byte(password),
		ln:       ln,
		conns:    map[net.Conn]struct{}{},
	}
	p.transport = &http.Transport{
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			ctx, cancel := context.WithTimeout(ctx, proxyDialTimeout)
			defer cancel()
			return p.dial(ctx, network, address)
		},
		Proxy:                 nil,
		MaxIdleConnsPerHost:   16,
		IdleConnTimeout:       90 * time.Second,
		ExpectContinueTimeout: time.Second,
		// What the player asked for, untouched: FFmpeg reads what it asked for.
		DisableCompression: true,
	}
	p.forward = &httputil.ReverseProxy{
		Rewrite: func(r *httputil.ProxyRequest) {
			r.Out.URL = cloneURL(r.In)
			r.Out.Host = r.In.Host
		},
		Transport: p.transport,
		// Video, playlists and long answers as they come.
		FlushInterval: -1,
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			p.fail(w, r.URL.Host, err)
		},
		ErrorLog: log.New(logWriter(logf), "tailnet: proxy: ", 0),
	}
	p.server = &http.Server{
		Handler:           p,
		ReadHeaderTimeout: handshakeTimeout,
		ErrorLog:          log.New(logWriter(logf), "tailnet: proxy: ", 0),
	}
	socks, web := proxymux.SplitSOCKSAndHTTP(ln)
	go p.serveSOCKS(socks)
	go p.server.Serve(web)
	return p, nil
}

// Addr is the address the proxy listens on (« 127.0.0.1:53123 »).
func (p *Proxy) Addr() string { return p.ln.Addr().String() }

// Close stops the proxy and cuts the connections through it.
func (p *Proxy) Close() error {
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return nil
	}
	p.closed = true
	conns := p.conns
	p.conns = nil
	p.mu.Unlock()
	err := p.ln.Close()
	p.server.Close()
	p.transport.CloseIdleConnections()
	for c := range conns {
		c.Close()
	}
	return err
}

func (p *Proxy) track(c net.Conn) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed {
		return false
	}
	p.conns[c] = struct{}{}
	return true
}

func (p *Proxy) untrack(c net.Conn) {
	p.mu.Lock()
	defer p.mu.Unlock()
	delete(p.conns, c)
}

// ServeHTTP proxies one HTTP request: CONNECT opens a tunnel, an absolute-form http:// request is forwarded.
func (p *Proxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if subtle.ConstantTimeCompare([]byte(r.Header.Get("Proxy-Authorization")), []byte(p.auth)) != 1 {
		w.Header().Set("Proxy-Authenticate", `Basic realm="Mira"`)
		http.Error(w, "Mira: proxy authentication required", http.StatusProxyAuthRequired)
		return
	}
	if r.Method == http.MethodConnect {
		p.tunnel(w, r)
		return
	}
	if r.URL.Scheme != "http" || r.URL.Host == "" {
		http.Error(w, "Mira: this proxy forwards http:// requests, and https:// ones through CONNECT", http.StatusBadRequest)
		return
	}
	p.forward.ServeHTTP(w, r)
}

func (p *Proxy) tunnel(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), proxyDialTimeout)
	upstream, err := p.dial(ctx, "tcp", r.Host)
	cancel()
	if err != nil {
		p.fail(w, r.Host, err)
		return
	}
	client, buffered, err := http.NewResponseController(w).Hijack()
	if err != nil {
		upstream.Close()
		http.Error(w, "Mira: no tunnel on this connection", http.StatusInternalServerError)
		return
	}
	if _, err := io.WriteString(client, "HTTP/1.1 200 Connection established\r\n\r\n"); err != nil {
		client.Close()
		upstream.Close()
		return
	}
	// What the client sent right after its request is already read: it goes first.
	if n := buffered.Reader.Buffered(); n > 0 {
		head, _ := buffered.Reader.Peek(n)
		if _, err := upstream.Write(head); err != nil {
			client.Close()
			upstream.Close()
			return
		}
	}
	p.join(client, upstream)
}

// fail answers a request the node couldn't serve: 502 with the node's code.
func (p *Proxy) fail(w http.ResponseWriter, host string, err error) {
	code := CodeOf(err)
	p.logf("tailnet: proxy: %s: %s", host, describe(code, err))
	if code != "" {
		w.Header().Set(ProxyHeader, code)
	}
	http.Error(w, "Mira: "+describe(code, err), http.StatusBadGateway)
}

func describe(code string, err error) string {
	if code != "" {
		return code
	}
	return err.Error()
}

// serveSOCKS serves SOCKS5 (RFC 1928) with a user name and password (RFC 1929), CONNECT only.
func (p *Proxy) serveSOCKS(ln net.Listener) {
	for {
		c, err := ln.Accept()
		if err != nil {
			return
		}
		go p.socks(c)
	}
}

// SOCKS5 replies.
const (
	socksSucceeded      = 0x00
	socksFailure        = 0x01
	socksNotAllowed     = 0x02
	socksHostUnreached  = 0x04
	socksRefused        = 0x05
	socksNoCommand      = 0x07
	socksNoAddressType  = 0x08
	socksVersion        = 0x05
	socksMethodPassword = 0x02
	socksNoMethod       = 0xff
)

func (p *Proxy) socks(c net.Conn) {
	if !p.track(c) {
		c.Close()
		return
	}
	defer p.untrack(c)
	c.SetDeadline(time.Now().Add(handshakeTimeout))
	r := bufio.NewReader(c)
	host, port, ok := p.socksRequest(c, r)
	if !ok {
		c.Close()
		return
	}
	c.SetDeadline(time.Time{})
	address := net.JoinHostPort(host, strconv.Itoa(port))
	ctx, cancel := context.WithTimeout(context.Background(), proxyDialTimeout)
	upstream, err := p.dial(ctx, "tcp", address)
	cancel()
	if err != nil {
		code := CodeOf(err)
		p.logf("tailnet: proxy: %s: %s", address, describe(code, err))
		reply := byte(socksHostUnreached)
		switch code {
		case CodeRefused:
			reply = socksNotAllowed
		case CodeClosed:
			reply = socksRefused
		case CodeNeedsLogin, CodeExpired, CodeMachineAuth, CodeStarting, CodeStopped:
			reply = socksFailure
		}
		c.Write([]byte{socksVersion, reply, 0, 1, 0, 0, 0, 0, 0, 0})
		c.Close()
		return
	}
	if _, err := c.Write([]byte{socksVersion, socksSucceeded, 0, 1, 0, 0, 0, 0, 0, 0}); err != nil {
		upstream.Close()
		c.Close()
		return
	}
	// Bytes the client sent ahead of the reply.
	if n := r.Buffered(); n > 0 {
		head, _ := r.Peek(n)
		if _, err := upstream.Write(head); err != nil {
			upstream.Close()
			c.Close()
			return
		}
	}
	p.untrack(c)
	p.join(c, upstream)
}

// socksRequest reads the greeting, the credentials and the request, and answers the first two.
func (p *Proxy) socksRequest(c net.Conn, r *bufio.Reader) (host string, port int, ok bool) {
	var head [2]byte
	if _, err := io.ReadFull(r, head[:]); err != nil || head[0] != socksVersion {
		return "", 0, false
	}
	methods := make([]byte, head[1])
	if _, err := io.ReadFull(r, methods); err != nil {
		return "", 0, false
	}
	if !strings.ContainsRune(string(methods), socksMethodPassword) {
		c.Write([]byte{socksVersion, socksNoMethod})
		return "", 0, false
	}
	if _, err := c.Write([]byte{socksVersion, socksMethodPassword}); err != nil {
		return "", 0, false
	}
	// RFC 1929: version 1, user name, password.
	if _, err := io.ReadFull(r, head[:]); err != nil || head[0] != 0x01 {
		return "", 0, false
	}
	user := make([]byte, head[1])
	if _, err := io.ReadFull(r, user); err != nil {
		return "", 0, false
	}
	size, err := r.ReadByte()
	if err != nil {
		return "", 0, false
	}
	password := make([]byte, size)
	if _, err := io.ReadFull(r, password); err != nil {
		return "", 0, false
	}
	userOK := subtle.ConstantTimeCompare(user, p.user)
	passwordOK := subtle.ConstantTimeCompare(password, p.password)
	if userOK&passwordOK != 1 {
		c.Write([]byte{0x01, 0x01})
		return "", 0, false
	}
	if _, err := c.Write([]byte{0x01, 0x00}); err != nil {
		return "", 0, false
	}
	var req [4]byte
	if _, err := io.ReadFull(r, req[:]); err != nil || req[0] != socksVersion {
		return "", 0, false
	}
	if req[1] != 0x01 { // CONNECT
		c.Write([]byte{socksVersion, socksNoCommand, 0, 1, 0, 0, 0, 0, 0, 0})
		return "", 0, false
	}
	switch req[3] {
	case 0x01, 0x04: // IPv4, IPv6
		size := 4
		if req[3] == 0x04 {
			size = 16
		}
		raw := make([]byte, size)
		if _, err := io.ReadFull(r, raw); err != nil {
			return "", 0, false
		}
		ip, _ := netip.AddrFromSlice(raw)
		host = ip.String()
	case 0x03: // a name
		size, err := r.ReadByte()
		if err != nil || size == 0 {
			return "", 0, false
		}
		raw := make([]byte, size)
		if _, err := io.ReadFull(r, raw); err != nil {
			return "", 0, false
		}
		host = string(raw)
	default:
		c.Write([]byte{socksVersion, socksNoAddressType, 0, 1, 0, 0, 0, 0, 0, 0})
		return "", 0, false
	}
	var raw [2]byte
	if _, err := io.ReadFull(r, raw[:]); err != nil {
		return "", 0, false
	}
	return host, int(binary.BigEndian.Uint16(raw[:])), true
}

// join copies both ways until both sides are done, passing on each half-close, then closes both.
func (p *Proxy) join(a, b net.Conn) {
	if !p.track(a) {
		a.Close()
		b.Close()
		return
	}
	p.track(b)
	defer func() {
		p.untrack(a)
		p.untrack(b)
		a.Close()
		b.Close()
	}()
	var wg sync.WaitGroup
	copyHalf := func(dst, src net.Conn) {
		defer wg.Done()
		_, err := io.Copy(dst, src)
		if cw, ok := dst.(interface{ CloseWrite() error }); ok && err == nil {
			cw.CloseWrite()
			return
		}
		// No half-close, or a broken side: end both.
		dst.Close()
		src.Close()
	}
	wg.Add(2)
	go copyHalf(a, b)
	go copyHalf(b, a)
	wg.Wait()
}

func cloneURL(r *http.Request) *url.URL {
	u := *r.URL
	return &u
}

type logWriter func(string, ...any)

func (f logWriter) Write(b []byte) (int, error) {
	f("%s", strings.TrimRight(string(b), "\n"))
	return len(b), nil
}
