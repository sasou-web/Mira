package tailnet

import (
	"bytes"
	"compress/gzip"
	"context"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"log"
	"mime"
	"net"
	"net/http"
	"net/http/httputil"
	"net/netip"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// A Relay is Mira's way onto the tailnet for what cannot be told to use a proxy: Apple's player, a Safari page, the
// iPhone's own requests. It listens on 127.0.0.1 only, and passes a request on only when its path starts with the
// secret:
//
//	http://127.0.0.1:<port>/<secret>/<http|https>/<host[:port]>/<path>?<query>
//
// is the request <http|https>://<host[:port]>/<path>?<query>, made through Dial. Every app of the device can reach
// 127.0.0.1: without the secret, a request gets 404 and learns nothing. Answers pass through as they are, but for
// what would leave the prefix: a Location to the server itself, and the absolute paths of HLS playlists (the Mira
// extension's master playlist names Jellyfin's as /videos/…), put back under the prefix. When the server cannot be
// reached, the relay answers 502 (504 when it took too long) with ProxyHeader set to the reason: the Code of Dial's
// error, "certificate", "timeout" or "unreachable".
type Relay struct {
	// Dial connects over the tailnet, and refuses what the tailnet does not reach.
	Dial func(ctx context.Context, network, address string) (net.Conn, error)
	// Secret is the first segment of every path: 16 characters or more, letters, digits, '-' and '_'.
	Secret string
	// RootCAs are the authorities trusted for an https:// server; nil means the system's.
	RootCAs *x509.CertPool
	// Logf, if set, is told why requests failed (never their address: it carries the session's token).
	Logf func(format string, args ...any)

	mu        sync.Mutex
	listener  net.Listener
	server    *http.Server
	proxy     *httputil.ReverseProxy
	transport *http.Transport
	port      int
	closed    bool
}

const (
	// A server that has not answered the connection by then is not coming: Dial waits for the node a few seconds.
	relayDialTimeout = 30 * time.Second
	// A playlist larger than this passes through as it is.
	relayPlaylistLimit = 16 << 20
)

var relaySecret = regexp.MustCompile(`^[A-Za-z0-9_-]{16,128}$`)

// Start listens on 127.0.0.1:port (any free port when port is 0, or when that one is taken) and serves until Close.
// It returns the relay's base, http://127.0.0.1:<port>/<secret>.
func (r *Relay) Start(port int) (string, error) {
	if r.Dial == nil {
		return "", errors.New("tailnet: relay without Dial")
	}
	if !relaySecret.MatchString(r.Secret) {
		return "", errors.New("tailnet: relay secret must be 16 to 128 letters, digits, '-' or '_'")
	}
	if port < 0 || port > 65535 {
		return "", fmt.Errorf("tailnet: relay port %d", port)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return "", errors.New("tailnet: relay closed")
	}
	if r.listener != nil {
		return r.baseLocked(), nil
	}
	r.transport = &http.Transport{
		DialContext: r.dial,
		TLSClientConfig: &tls.Config{
			RootCAs:    r.RootCAs,
			MinVersion: tls.VersionTLS12,
		},
		// HTTP/1.1 only: a WebSocket is an Upgrade of an HTTP/1.1 request.
		TLSNextProto:          map[string]func(string, *tls.Conn) http.RoundTripper{},
		TLSHandshakeTimeout:   15 * time.Second,
		DisableCompression:    true,
		MaxIdleConnsPerHost:   16,
		IdleConnTimeout:       60 * time.Second,
		ExpectContinueTimeout: time.Second,
	}
	r.proxy = &httputil.ReverseProxy{
		Rewrite:        rewriteOut,
		Transport:      r.transport,
		FlushInterval:  -1,
		ModifyResponse: rewriteBack,
		ErrorHandler:   r.failed,
		ErrorLog:       log.New(logWriter(r.logf), "tailnet: relay: ", 0),
	}
	r.server = &http.Server{
		Handler:           r,
		ReadHeaderTimeout: 15 * time.Second,
		IdleTimeout:       2 * time.Minute,
		ErrorLog:          log.New(logWriter(r.logf), "tailnet: relay: ", 0),
	}
	if err := r.listenLocked(port); err != nil {
		return "", err
	}
	return r.baseLocked(), nil
}

// Base is http://127.0.0.1:<port>/<secret>, or "" when the relay is not listening.
func (r *Relay) Base() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.listener == nil {
		return ""
	}
	return r.baseLocked()
}

// Check makes sure the relay still answers on its port, and listens again (on the same port when it can) when it
// does not: iOS takes the listening socket of an app it suspended. It returns the base, which a new port changes.
func (r *Relay) Check() (string, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed || r.server == nil {
		return "", errors.New("tailnet: relay not started")
	}
	if r.listener != nil {
		conn, err := net.DialTimeout("tcp4", r.listener.Addr().String(), time.Second)
		if err == nil {
			conn.Close()
			return r.baseLocked(), nil
		}
		r.listener.Close()
		r.listener = nil
	}
	if err := r.listenLocked(r.port); err != nil {
		return "", err
	}
	return r.baseLocked(), nil
}

// Close stops listening and ends the connections in progress.
func (r *Relay) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return nil
	}
	r.closed = true
	var err error
	if r.server != nil {
		err = r.server.Close()
	}
	if r.listener != nil {
		r.listener.Close()
		r.listener = nil
	}
	if r.transport != nil {
		r.transport.CloseIdleConnections()
	}
	return err
}

func (r *Relay) listenLocked(port int) error {
	listener, err := net.Listen("tcp4", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil && port != 0 {
		listener, err = net.Listen("tcp4", "127.0.0.1:0")
	}
	if err != nil {
		return fmt.Errorf("tailnet: relay: %w", err)
	}
	r.listener = listener
	r.port = listener.Addr().(*net.TCPAddr).Port
	server := r.server
	go func() {
		// Returns when Close closes the server, or when the socket is taken away: Check listens again.
		_ = server.Serve(listener)
	}()
	return nil
}

func (r *Relay) baseLocked() string {
	return "http://127.0.0.1:" + strconv.Itoa(r.port) + "/" + r.Secret
}

func (r *Relay) logf(format string, args ...any) {
	if r.Logf != nil {
		r.Logf(format, args...)
	}
}

// ServeHTTP passes on a request that names the relay itself (no other name: a web page cannot point a name of its own
// at 127.0.0.1 and talk to it) and starts with the secret; anything else is 404.
func (r *Relay) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	r.mu.Lock()
	port, proxy := r.port, r.proxy
	r.mu.Unlock()
	if !loopbackHost(req.Host, port) {
		http.NotFound(w, req)
		return
	}
	target, ok := parseTarget(req.URL.EscapedPath(), r.Secret, port)
	if !ok {
		http.NotFound(w, req)
		return
	}
	if !allowedHost(target.host) {
		w.Header().Set(ProxyHeader, "refused")
		w.Header().Set("Cache-Control", "no-store")
		http.Error(w, "Mira relay: refused", http.StatusForbidden)
		return
	}
	proxy.ServeHTTP(w, req.WithContext(context.WithValue(req.Context(), targetKey{}, target)))
}

func (r *Relay) dial(ctx context.Context, network, address string) (net.Conn, error) {
	ctx, cancel := context.WithTimeout(ctx, relayDialTimeout)
	defer cancel()
	return r.Dial(ctx, network, address)
}

// failed answers a request whose server could not be reached (or whose playlist could not be read), saying why.
func (r *Relay) failed(w http.ResponseWriter, req *http.Request, err error) {
	if req.Context().Err() != nil {
		// The player or the app gave up first: no one to tell.
		return
	}
	code := relayCode(err)
	r.logf("tailnet: relay: %s: %v", code, err)
	w.Header().Set(ProxyHeader, code)
	w.Header().Set("Cache-Control", "no-store")
	status := http.StatusBadGateway
	if code == "timeout" {
		status = http.StatusGatewayTimeout
	}
	http.Error(w, "Mira relay: "+code, status)
}

// relayCode is the reason said to the app: the Code of Dial's error, or what the error looks like.
func relayCode(err error) string {
	if code := CodeOf(err); code != "" {
		return code
	}
	var certificate *tls.CertificateVerificationError
	var unknown x509.UnknownAuthorityError
	var hostname x509.HostnameError
	var invalid x509.CertificateInvalidError
	if errors.As(err, &certificate) || errors.As(err, &unknown) || errors.As(err, &hostname) || errors.As(err, &invalid) {
		return "certificate"
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return "timeout"
	}
	var timeout interface{ Timeout() bool }
	if errors.As(err, &timeout) && timeout.Timeout() {
		return "timeout"
	}
	return "unreachable"
}

// The server a request goes to, as its path named it.
type relayTarget struct {
	scheme    string // http or https
	authority string // host:port, the port always written
	host      string // the host alone, without brackets
	hostname  string // what the Host header says: the port only when it is not the scheme's own
	prefix    string // /<secret>/<scheme>/<authority> as the client wrote it
	origin    string // http://127.0.0.1:<port>
	secret    string
	rest      string // the path after the prefix, escaped, starting with /
}

type targetKey struct{}

// parseTarget reads /<secret>/<scheme>/<authority>[/<rest>].
func parseTarget(escaped, secret string, port int) (relayTarget, bool) {
	parts := strings.SplitN(strings.TrimPrefix(escaped, "/"), "/", 4)
	if len(parts) < 3 || subtle.ConstantTimeCompare([]byte(parts[0]), []byte(secret)) != 1 {
		return relayTarget{}, false
	}
	scheme := parts[1]
	if scheme != "http" && scheme != "https" {
		return relayTarget{}, false
	}
	authority, err := url.PathUnescape(parts[2])
	if err != nil || authority == "" || strings.ContainsAny(authority, "@/?#\\ ") {
		return relayTarget{}, false
	}
	host, portText := authority, ""
	if h, p, err := net.SplitHostPort(authority); err == nil {
		if p == "" {
			return relayTarget{}, false
		}
		host, portText = h, p
	} else if strings.HasPrefix(authority, "[") && strings.HasSuffix(authority, "]") {
		host = authority[1 : len(authority)-1]
	} else if strings.Contains(authority, ":") {
		return relayTarget{}, false
	}
	if !validHost(host) {
		return relayTarget{}, false
	}
	defaultPort := "80"
	if scheme == "https" {
		defaultPort = "443"
	}
	if portText == "" {
		portText = defaultPort
	}
	if n, err := strconv.Atoi(portText); err != nil || n < 1 || n > 65535 || strconv.Itoa(n) != portText {
		return relayTarget{}, false
	}
	rest := "/"
	if len(parts) == 4 {
		rest += parts[3]
	}
	hostname := net.JoinHostPort(host, portText)
	if portText == defaultPort {
		hostname = host
		if strings.Contains(host, ":") {
			hostname = "[" + host + "]"
		}
	}
	prefix := "/" + parts[0] + "/" + parts[1] + "/" + parts[2]
	return relayTarget{
		scheme:    scheme,
		authority: net.JoinHostPort(host, portText),
		host:      host,
		hostname:  hostname,
		prefix:    prefix,
		origin:    "http://127.0.0.1:" + strconv.Itoa(port),
		secret:    parts[0],
		rest:      rest,
	}, true
}

var dnsName = regexp.MustCompile(`^(?i)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*\.?$`)

// validHost is an IP address or a DNS name.
func validHost(host string) bool {
	if _, err := netip.ParseAddr(host); err == nil {
		return true
	}
	return len(host) <= 253 && dnsName.MatchString(host)
}

// allowedHost leaves out what is this device or every device: the relay is a way to the tailnet, not to the
// device's own services (Dial refuses the rest of what is not on the tailnet).
func allowedHost(host string) bool {
	if addr, err := netip.ParseAddr(host); err == nil {
		addr = addr.Unmap()
		return !(addr.IsLoopback() || addr.IsUnspecified() || addr.IsLinkLocalUnicast() || addr.IsLinkLocalMulticast() ||
			addr.IsInterfaceLocalMulticast() || addr.IsMulticast() || addr == netip.AddrFrom4([4]byte{255, 255, 255, 255}))
	}
	name := strings.TrimSuffix(strings.ToLower(host), ".")
	return name != "localhost" && !strings.HasSuffix(name, ".localhost")
}

// loopbackHost is the Host header of a request made to the relay itself.
func loopbackHost(hostHeader string, port int) bool {
	host, portText, err := net.SplitHostPort(hostHeader)
	if err != nil || portText != strconv.Itoa(port) {
		return false
	}
	return host == "127.0.0.1" || strings.EqualFold(host, "localhost")
}

// rewriteOut turns the relay's request into the server's: same method, path, query, headers and body, the server's
// own name in Host, and nothing added that says where it came from.
func rewriteOut(pr *httputil.ProxyRequest) {
	target := pr.In.Context().Value(targetKey{}).(relayTarget)
	out := pr.Out
	path, err := url.PathUnescape(target.rest)
	if err != nil {
		path = target.rest
	}
	out.URL = &url.URL{Scheme: target.scheme, Host: target.authority, Path: path, RawPath: target.rest, RawQuery: pr.In.URL.RawQuery}
	out.Host = target.hostname
	// A playlist is read to be rewritten: it comes as it is, not compressed.
	if strings.HasSuffix(strings.ToLower(path), ".m3u8") {
		out.Header.Del("Accept-Encoding")
	}
}

// rewriteBack puts what the server names under the prefix again: its redirects, and its playlists' absolute paths.
func rewriteBack(resp *http.Response) error {
	target, ok := resp.Request.Context().Value(targetKey{}).(relayTarget)
	if !ok {
		return nil
	}
	for _, header := range []string{"Location", "Content-Location"} {
		if link := resp.Header.Get(header); link != "" {
			resp.Header.Set(header, target.rebase(link))
		}
	}
	if resp.StatusCode != http.StatusOK || resp.Request.Method == http.MethodHead || !isPlaylist(resp) {
		return nil
	}
	return rewritePlaylist(resp, target)
}

// rebase is where a link of the server's leads through the relay: an absolute path, or an address of the same host,
// goes under the prefix; a relative link already does; an address elsewhere stays as it is.
func (t relayTarget) rebase(link string) string {
	if strings.HasPrefix(link, "//") {
		link = t.scheme + ":" + link
	}
	if strings.HasPrefix(link, "/") {
		return t.prefix + link
	}
	parsed, err := url.Parse(link)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return link
	}
	scheme := strings.ToLower(parsed.Scheme)
	if (scheme != "http" && scheme != "https") || parsed.User != nil || !strings.EqualFold(parsed.Hostname(), t.host) {
		return link
	}
	port := parsed.Port()
	if port == "" {
		port = "80"
		if scheme == "https" {
			port = "443"
		}
	}
	segment := net.JoinHostPort(t.host, port)
	if strings.Contains(t.host, ":") {
		segment = strings.NewReplacer("[", "%5B", "]", "%5D").Replace(segment)
	}
	rest := parsed.EscapedPath()
	if rest == "" {
		rest = "/"
	}
	if parsed.RawQuery != "" || parsed.ForceQuery {
		rest += "?" + parsed.RawQuery
	}
	if parsed.Fragment != "" {
		rest += "#" + parsed.EscapedFragment()
	}
	return t.origin + "/" + t.secret + "/" + scheme + "/" + segment + rest
}

var playlistTypes = map[string]bool{
	"application/vnd.apple.mpegurl": true,
	"application/x-mpegurl":         true,
	"audio/mpegurl":                 true,
	"audio/x-mpegurl":               true,
}

func isPlaylist(resp *http.Response) bool {
	if media, _, err := mime.ParseMediaType(resp.Header.Get("Content-Type")); err == nil && playlistTypes[strings.ToLower(media)] {
		return true
	}
	return strings.HasSuffix(strings.ToLower(resp.Request.URL.Path), ".m3u8")
}

var uriAttribute = regexp.MustCompile(`URI="([^"]*)"`)

// rewritePlaylist reads an HLS playlist and rebases its URIs: the lines that are one, and the URI="…" of its tags.
func rewritePlaylist(resp *http.Response, target relayTarget) error {
	encoding := strings.ToLower(strings.TrimSpace(resp.Header.Get("Content-Encoding")))
	if encoding != "" && encoding != "identity" && encoding != "gzip" {
		// Not one the relay reads: as it is.
		return nil
	}
	raw, err := io.ReadAll(io.LimitReader(resp.Body, relayPlaylistLimit+1))
	if err != nil {
		resp.Body.Close()
		return err
	}
	if len(raw) > relayPlaylistLimit {
		resp.Body = struct {
			io.Reader
			io.Closer
		}{io.MultiReader(bytes.NewReader(raw), resp.Body), resp.Body}
		return nil
	}
	resp.Body.Close()
	text := raw
	if encoding == "gzip" {
		reader, err := gzip.NewReader(bytes.NewReader(raw))
		if err != nil {
			return err
		}
		text, err = io.ReadAll(io.LimitReader(reader, relayPlaylistLimit+1))
		if err != nil {
			return err
		}
		if len(text) > relayPlaylistLimit {
			return errors.New("tailnet: relay: playlist too large")
		}
	}
	out := RebasePlaylist(text, target.rebase)
	resp.Body = io.NopCloser(bytes.NewReader(out))
	resp.ContentLength = int64(len(out))
	resp.Header.Set("Content-Length", strconv.Itoa(len(out)))
	resp.Header.Del("Content-Encoding")
	if !bytes.Equal(out, raw) {
		// Its validators described other bytes.
		resp.Header.Del("ETag")
		resp.Header.Del("Content-MD5")
		resp.Header.Del("Digest")
	}
	return nil
}

// RebasePlaylist gives each URI of an HLS playlist (a line that is not a tag, and the URI="…" of a tag) to rebase.
func RebasePlaylist(text []byte, rebase func(string) string) []byte {
	var out bytes.Buffer
	out.Grow(len(text) + len(text)/4)
	for len(text) > 0 {
		end := bytes.IndexByte(text, '\n')
		var line, newline []byte
		if end < 0 {
			line, text = text, nil
		} else {
			line, newline, text = text[:end], text[end:end+1], text[end+1:]
		}
		if bytes.HasSuffix(line, []byte("\r")) {
			line, newline = line[:len(line)-1], append([]byte("\r"), newline...)
		}
		trimmed := bytes.TrimSpace(line)
		switch {
		case len(trimmed) == 0:
			out.Write(line)
		case trimmed[0] == '#':
			out.Write(uriAttribute.ReplaceAllFunc(line, func(match []byte) []byte {
				inner := match[len(`URI="`) : len(match)-1]
				return []byte(`URI="` + rebase(string(inner)) + `"`)
			}))
		default:
			start := bytes.Index(line, trimmed)
			out.Write(line[:start])
			out.WriteString(rebase(string(trimmed)))
			out.Write(line[start+len(trimmed):])
		}
		out.Write(newline)
	}
	return out.Bytes()
}
