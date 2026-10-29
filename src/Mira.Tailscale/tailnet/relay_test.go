package tailnet

import (
	"bufio"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

const testSecret = "s3cr3t-0123456789abcdef"

// A tailnet for tests: every address dials the same server, and the addresses asked are kept.
type fakeTailnet struct {
	mu     sync.Mutex
	asked  []string
	server string
	fail   error
}

func (f *fakeTailnet) dial(ctx context.Context, network, address string) (net.Conn, error) {
	f.mu.Lock()
	f.asked = append(f.asked, address)
	fail := f.fail
	f.mu.Unlock()
	if fail != nil {
		return nil, fail
	}
	var d net.Dialer
	return d.DialContext(ctx, network, f.server)
}

func (f *fakeTailnet) dialed() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.asked...)
}

// startRelay starts a relay in front of handler, reached at any address of the fake tailnet.
func startRelay(t *testing.T, handler http.Handler) (*Relay, *fakeTailnet, string) {
	t.Helper()
	backend := httptest.NewServer(handler)
	t.Cleanup(backend.Close)
	tailnet := &fakeTailnet{server: backend.Listener.Addr().String()}
	relay := &Relay{Dial: tailnet.dial, Secret: testSecret, Logf: t.Logf}
	base, err := relay.Start(0)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { relay.Close() })
	return relay, tailnet, base
}

// noRedirects is a client that shows redirects instead of following them.
var noRedirects = &http.Client{
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	Timeout:       10 * time.Second,
}

func get(t *testing.T, address string, headers ...string) (*http.Response, string) {
	t.Helper()
	req, err := http.NewRequest(http.MethodGet, address, nil)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i+1 < len(headers); i += 2 {
		if headers[i] == "Host" {
			req.Host = headers[i+1]
		} else {
			req.Header.Set(headers[i], headers[i+1])
		}
	}
	resp, err := noRedirects.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	return resp, string(body)
}

func TestRelayPassesTheRequestOn(t *testing.T) {
	var seen *http.Request
	_, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		seen = r.Clone(context.Background())
		w.Header().Set("X-Answer", "yes")
		w.WriteHeader(http.StatusTeapot)
		io.WriteString(w, "jellyfin")
	}))
	resp, body := get(t, base+"/http/100.64.0.5:8096/Items/abc%2Fdef/Images?tag=x%20y&ApiKey=token", "Authorization", `MediaBrowser Token="t"`)
	if resp.StatusCode != http.StatusTeapot || body != "jellyfin" || resp.Header.Get("X-Answer") != "yes" {
		t.Fatalf("answer %d %q %v", resp.StatusCode, body, resp.Header)
	}
	if got := tailnet.dialed(); len(got) != 1 || got[0] != "100.64.0.5:8096" {
		t.Fatalf("dialed %v", got)
	}
	if seen.URL.EscapedPath() != "/Items/abc%2Fdef/Images" || seen.URL.RawQuery != "tag=x%20y&ApiKey=token" {
		t.Fatalf("path %q query %q", seen.URL.EscapedPath(), seen.URL.RawQuery)
	}
	if seen.Host != "100.64.0.5:8096" {
		t.Fatalf("Host %q", seen.Host)
	}
	if seen.Header.Get("Authorization") != `MediaBrowser Token="t"` {
		t.Fatalf("Authorization %q", seen.Header.Get("Authorization"))
	}
	for _, header := range []string{"X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded"} {
		if seen.Header.Get(header) != "" {
			t.Fatalf("%s added: %q", header, seen.Header.Get(header))
		}
	}
}

func TestRelayDefaultPortsAndRoot(t *testing.T) {
	var host, path string
	_, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		host, path = r.Host, r.URL.Path
	}))
	get(t, base+"/http/desktop-invefqh.tail1234.ts.net")
	if got := tailnet.dialed(); got[0] != "desktop-invefqh.tail1234.ts.net:80" || host != "desktop-invefqh.tail1234.ts.net" || path != "/" {
		t.Fatalf("dialed %v, Host %q, path %q", got, host, path)
	}
}

func TestRelayAnswersOnlyWithItsSecret(t *testing.T) {
	relay, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	origin := strings.TrimSuffix(base, "/"+testSecret)
	for _, address := range []string{
		origin + "/",
		origin + "/System/Info/Public",
		origin + "/wrong-secret-0123456789/http/100.64.0.5:8096/System/Info/Public",
		origin + "/" + testSecret + "x/http/100.64.0.5:8096/System/Info/Public",
		origin + "/" + testSecret[:len(testSecret)-1] + "/http/100.64.0.5:8096/",
		base,
		base + "/ftp/100.64.0.5:21/file",
		base + "/http/",
		base + "/http/user@100.64.0.5:8096/",
		base + "/http/100.64.0.5:0/",
		base + "/http/100.64.0.5:65536/",
		base + "/http/100.64.0.5:08096/",
		base + "/http/100.64.0.5:/",
		base + "/http/bad_name:8096/",
		base + "/http/fd7a:115c:a1e0::1:8096/",
	} {
		if resp, _ := get(t, address); resp.StatusCode != http.StatusNotFound {
			t.Errorf("%s: %d", address, resp.StatusCode)
		}
	}
	// A page of the Internet pointing one of its names at 127.0.0.1 does not get through either.
	port := strconv.Itoa(relay.port)
	if resp, _ := get(t, "http://127.0.0.1:"+port+"/"+testSecret+"/http/100.64.0.5:8096/", "Host", "evil.example:"+port); resp.StatusCode != http.StatusNotFound {
		t.Errorf("rebinding: %d", resp.StatusCode)
	}
	if resp, _ := get(t, "http://localhost:"+port+"/"+testSecret+"/http/100.64.0.5:8096/"); resp.StatusCode != http.StatusOK {
		t.Errorf("localhost: %d", resp.StatusCode)
	}
	if got := tailnet.dialed(); len(got) != 1 {
		t.Fatalf("dialed %v", got)
	}
}

func TestRelayNeverReachesThisDevice(t *testing.T) {
	_, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	for _, target := range []string{"127.0.0.1:8096", "127.1.2.3", "%5B::1%5D:8096", "localhost:8096", "LocalHost.:8096", "app.localhost", "0.0.0.0:8096", "169.254.10.1:80", "%5Bfe80::1%5D:80", "224.0.0.1", "255.255.255.255", "%5B::ffff:127.0.0.1%5D:80"} {
		resp, _ := get(t, base+"/http/"+target+"/System/Info/Public")
		if resp.StatusCode != http.StatusForbidden || resp.Header.Get(ProxyHeader) != "refused" {
			t.Errorf("%s: %d %q", target, resp.StatusCode, resp.Header.Get(ProxyHeader))
		}
	}
	if got := tailnet.dialed(); len(got) != 0 {
		t.Fatalf("dialed %v", got)
	}
}

func TestRelayIPv6(t *testing.T) {
	_, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	get(t, base+"/http/%5Bfd7a:115c:a1e0::5%5D:8096/System/Ping")
	if got := tailnet.dialed(); len(got) != 1 || got[0] != "[fd7a:115c:a1e0::5]:8096" {
		t.Fatalf("dialed %v", got)
	}
}

func TestRelayRangesAndBodies(t *testing.T) {
	content := strings.Repeat("0123456789", 1000)
	_, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost {
			body, _ := io.ReadAll(r.Body)
			w.Write(bytes.ToUpper(body))
			return
		}
		http.ServeContent(w, r, "film.mkv", time.Unix(0, 0), strings.NewReader(content))
	}))
	resp, body := get(t, base+"/http/100.64.0.5:8096/Videos/x/stream", "Range", "bytes=10-19")
	if resp.StatusCode != http.StatusPartialContent || body != "0123456789" || resp.Header.Get("Content-Range") != "bytes 10-19/10000" {
		t.Fatalf("range: %d %q %q", resp.StatusCode, body, resp.Header.Get("Content-Range"))
	}
	post, err := http.Post(base+"/http/100.64.0.5:8096/Sessions/Playing", "application/json", strings.NewReader(`{"itemid":"x"}`))
	if err != nil {
		t.Fatal(err)
	}
	defer post.Body.Close()
	if answer, _ := io.ReadAll(post.Body); string(answer) != `{"ITEMID":"X"}` {
		t.Fatalf("post: %q", answer)
	}
}

func TestRelayStreamsWithoutWaiting(t *testing.T) {
	read := make(chan struct{})
	_, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "video/mp2t")
		io.WriteString(w, "first")
		w.(http.Flusher).Flush()
		select {
		case <-read:
		case <-time.After(5 * time.Second):
			return
		}
		io.WriteString(w, "second")
	}))
	resp, err := http.Get(base + "/http/100.64.0.5:8096/Videos/x/stream.ts")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	first := make([]byte, 5)
	if _, err := io.ReadFull(resp.Body, first); err != nil || string(first) != "first" {
		t.Fatalf("first %q %v", first, err)
	}
	close(read)
	if rest, _ := io.ReadAll(resp.Body); string(rest) != "second" {
		t.Fatalf("rest %q", rest)
	}
}

func TestRelayRebasesRedirects(t *testing.T) {
	_, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Location", r.URL.Query().Get("to"))
		w.WriteHeader(http.StatusFound)
	}))
	origin := strings.TrimSuffix(base, "/"+testSecret)
	for to, want := range map[string]string{
		"/videos/abc/master.m3u8?ApiKey=t":           "/" + testSecret + "/http/100.64.0.5:8096/videos/abc/master.m3u8?ApiKey=t",
		"http://100.64.0.5:8096/web/index.html":      base + "/http/100.64.0.5:8096/web/index.html",
		"https://100.64.0.5/web/":                    base + "/https/100.64.0.5:443/web/",
		"//100.64.0.5:8096/x":                        base + "/http/100.64.0.5:8096/x",
		"http://100.64.0.5:8096":                     base + "/http/100.64.0.5:8096/",
		"https://login.example.com/a?b=c":            "https://login.example.com/a?b=c",
		"master.m3u8?x=1":                            "master.m3u8?x=1",
		"../../videos/abc/main.m3u8":                 "../../videos/abc/main.m3u8",
		"http://user@100.64.0.5:8096/":               "http://user@100.64.0.5:8096/",
		"http://100.64.0.6:8096/other-device":        "http://100.64.0.6:8096/other-device",
		"http://100.64.0.5:8096/a%2Fb?q=%20#chapter": base + "/http/100.64.0.5:8096/a%2Fb?q=%20#chapter",
	} {
		resp, _ := get(t, base+"/http/100.64.0.5:8096/redirect?to="+urlQueryEscape(to))
		if got := resp.Header.Get("Location"); got != want {
			t.Errorf("%s: Location %q, want %q", to, got, want)
		}
	}
	_ = origin
}

func urlQueryEscape(s string) string {
	return strings.NewReplacer("%", "%25", "&", "%26", "#", "%23", "?", "%3F", "+", "%2B", " ", "%20").Replace(s)
}

const masterPlaylist = "#EXTM3U\r\n" +
	"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",DEFAULT=NO,URI=\"/Mira/hls/abc/0123/Subtitles/2/subtitles.m3u8?ApiKey=t\"\r\n" +
	"#EXT-X-STREAM-INF:BANDWIDTH=1000,SUBTITLES=\"subs\"\r\n" +
	"/videos/abc/main.m3u8?ApiKey=t\r\n" +
	"\r\n" +
	"  hls1/main/0.ts  \r\n" +
	"http://100.64.0.5:8096/videos/abc/hls1/main/1.ts\r\n" +
	"https://cdn.example.com/2.ts\r\n" +
	"#EXT-X-MAP:URI=\"init.mp4\"\r\n" +
	"#EXT-X-KEY:METHOD=AES-128,URI=\"/keys/1\",IV=0x1\r\n" +
	"#EXT-X-ENDLIST"

func wantedMaster(base string) string {
	prefix := "/" + testSecret + "/http/100.64.0.5:8096"
	return "#EXTM3U\r\n" +
		"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",DEFAULT=NO,URI=\"" + prefix + "/Mira/hls/abc/0123/Subtitles/2/subtitles.m3u8?ApiKey=t\"\r\n" +
		"#EXT-X-STREAM-INF:BANDWIDTH=1000,SUBTITLES=\"subs\"\r\n" +
		prefix + "/videos/abc/main.m3u8?ApiKey=t\r\n" +
		"\r\n" +
		"  hls1/main/0.ts  \r\n" +
		base + "/http/100.64.0.5:8096/videos/abc/hls1/main/1.ts\r\n" +
		"https://cdn.example.com/2.ts\r\n" +
		"#EXT-X-MAP:URI=\"init.mp4\"\r\n" +
		"#EXT-X-KEY:METHOD=AES-128,URI=\"" + prefix + "/keys/1\",IV=0x1\r\n" +
		"#EXT-X-ENDLIST"
}

func TestRelayRebasesPlaylists(t *testing.T) {
	_, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, ".m3u8") && r.Header.Get("Accept-Encoding") != "" {
			http.Error(w, "the relay must ask for a playlist uncompressed", http.StatusBadRequest)
			return
		}
		w.Header().Set("ETag", `"abc"`)
		switch r.URL.Path {
		case "/Mira/hls/abc/master.m3u8":
			w.Header().Set("Content-Type", "application/vnd.apple.mpegurl; charset=utf-8")
			io.WriteString(w, masterPlaylist)
		case "/playlist":
			// Named only by its type.
			w.Header().Set("Content-Type", "application/x-mpegURL")
			io.WriteString(w, masterPlaylist)
		case "/compressed":
			w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
			w.Header().Set("Content-Encoding", "gzip")
			zip := gzip.NewWriter(w)
			io.WriteString(zip, masterPlaylist)
			zip.Close()
		case "/video.ts":
			w.Header().Set("Content-Type", "video/mp2t")
			io.WriteString(w, masterPlaylist)
		}
	}))
	want := wantedMaster(base)
	for _, path := range []string{"/Mira/hls/abc/master.m3u8", "/playlist", "/compressed"} {
		resp, body := get(t, base+"/http/100.64.0.5:8096"+path, "Accept-Encoding", "gzip")
		if body != want {
			t.Errorf("%s:\n%s\nwant:\n%s", path, body, want)
		}
		if resp.Header.Get("Content-Length") != strconv.Itoa(len(want)) || resp.Header.Get("Content-Encoding") != "" || resp.Header.Get("ETag") != "" {
			t.Errorf("%s: headers %v", path, resp.Header)
		}
	}
	// Anything else is left as it is, bytes for bytes.
	if _, body := get(t, base+"/http/100.64.0.5:8096/video.ts"); body != masterPlaylist {
		t.Errorf("video changed: %q", body)
	}
}

func TestRebasePlaylistKeepsLineEndings(t *testing.T) {
	in := "#EXTM3U\n/a.ts\nb.ts\n#EXT-X-ENDLIST\n"
	got := string(RebasePlaylist([]byte(in), func(link string) string { return "<" + link + ">" }))
	if got != "#EXTM3U\n</a.ts>\n<b.ts>\n#EXT-X-ENDLIST\n" {
		t.Fatalf("%q", got)
	}
	if got := string(RebasePlaylist([]byte("a.ts"), func(string) string { return "x" })); got != "x" {
		t.Fatalf("no final newline: %q", got)
	}
}

func TestRelayCarriesWebSockets(t *testing.T) {
	_, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Upgrade") != "websocket" || r.URL.Path != "/socket" || r.URL.Query().Get("deviceId") != "d" {
			http.Error(w, "not a socket", http.StatusBadRequest)
			return
		}
		conn, buffered, err := w.(http.Hijacker).Hijack()
		if err != nil {
			return
		}
		defer conn.Close()
		io.WriteString(conn, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")
		line, _ := buffered.ReadString('\n')
		io.WriteString(conn, "echo "+line)
	}))
	address := strings.TrimPrefix(base, "http://")
	host := address[:strings.Index(address, "/")]
	conn, err := net.Dial("tcp", host)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(10 * time.Second))
	path := strings.TrimPrefix(address, host)
	fmt.Fprintf(conn, "GET %s/http/100.64.0.5:8096/socket?ApiKey=t&deviceId=d HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n", path, host)
	reader := bufio.NewReader(conn)
	resp, err := http.ReadResponse(reader, nil)
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode != http.StatusSwitchingProtocols {
		t.Fatalf("status %d", resp.StatusCode)
	}
	io.WriteString(conn, "KeepAlive\n")
	if line, err := reader.ReadString('\n'); err != nil || line != "echo KeepAlive\n" {
		t.Fatalf("echo %q %v", line, err)
	}
}

func TestRelaySaysWhyTheServerCannotBeReached(t *testing.T) {
	_, tailnet, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	for _, c := range []struct {
		err    error
		status int
		code   string
	}{
		{newError(CodeNeedsLogin, nil), http.StatusBadGateway, "needs-login"},
		{fmt.Errorf("dial: %w", newError(CodeOffline, errors.New("no network"))), http.StatusBadGateway, "offline"},
		{newError(CodePeerOffline, nil), http.StatusBadGateway, "peer-offline"},
		{context.DeadlineExceeded, http.StatusGatewayTimeout, "timeout"},
		{errors.New("connection refused"), http.StatusBadGateway, "unreachable"},
	} {
		tailnet.mu.Lock()
		tailnet.fail = c.err
		tailnet.mu.Unlock()
		resp, _ := get(t, base+"/http/100.64.0.5:8096/System/Ping")
		if resp.StatusCode != c.status || resp.Header.Get(ProxyHeader) != c.code || resp.Header.Get("Cache-Control") != "no-store" {
			t.Errorf("%v: %d %q", c.err, resp.StatusCode, resp.Header.Get(ProxyHeader))
		}
	}
}

func TestRelayHTTPS(t *testing.T) {
	backend := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, "secure "+r.Host)
	}))
	backend.Config.ErrorLog = log.New(io.Discard, "", 0)
	backend.StartTLS()
	defer backend.Close()
	tailnet := &fakeTailnet{server: backend.Listener.Addr().String()}
	roots := x509.NewCertPool()
	roots.AddCert(backend.Certificate())
	relay := &Relay{Dial: tailnet.dial, Secret: testSecret, RootCAs: roots}
	base, err := relay.Start(0)
	if err != nil {
		t.Fatal(err)
	}
	defer relay.Close()
	// httptest's certificate names example.com.
	if resp, body := get(t, base+"/https/example.com/"); resp.StatusCode != http.StatusOK || body != "secure example.com" {
		t.Fatalf("%d %q", resp.StatusCode, body)
	}
	if got := tailnet.dialed(); got[0] != "example.com:443" {
		t.Fatalf("dialed %v", got)
	}
	// Another name than the certificate's: refused, and said so.
	if resp, _ := get(t, base+"/https/jellyfin.tail1234.ts.net/"); resp.StatusCode != http.StatusBadGateway || resp.Header.Get(ProxyHeader) != "certificate" {
		t.Fatalf("%d %q", resp.StatusCode, resp.Header.Get(ProxyHeader))
	}
}

func TestRelayTakesAnotherPortWhenItsOwnIsTaken(t *testing.T) {
	taken, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer taken.Close()
	port := taken.Addr().(*net.TCPAddr).Port
	relay := &Relay{Dial: (&net.Dialer{}).DialContext, Secret: testSecret}
	base, err := relay.Start(port)
	if err != nil {
		t.Fatal(err)
	}
	defer relay.Close()
	if strings.Contains(base, ":"+strconv.Itoa(port)+"/") || relay.Base() != base {
		t.Fatalf("base %q with %d taken", base, port)
	}
}

func TestRelayListensAgainWhenItsSocketIsTaken(t *testing.T) {
	relay, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, "back") }))
	if again, err := relay.Check(); err != nil || again != base {
		t.Fatalf("check while fine: %q %v", again, err)
	}
	// What iOS does to a suspended app.
	relay.mu.Lock()
	relay.listener.Close()
	relay.mu.Unlock()
	if _, err := http.Get(base + "/http/100.64.0.5:8096/"); err == nil {
		t.Fatal("answered without its socket")
	}
	again, err := relay.Check()
	if err != nil || again != base {
		t.Fatalf("check: %q %v (was %q)", again, err, base)
	}
	if resp, body := get(t, base+"/http/100.64.0.5:8096/"); resp.StatusCode != http.StatusOK || body != "back" {
		t.Fatalf("%d %q", resp.StatusCode, body)
	}
}

func TestRelayClose(t *testing.T) {
	relay, _, base := startRelay(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	relay.Close()
	if _, err := http.Get(base + "/http/100.64.0.5:8096/"); err == nil {
		t.Fatal("answered once closed")
	}
	if relay.Base() != "" {
		t.Fatalf("base %q once closed", relay.Base())
	}
	if _, err := relay.Start(0); err == nil {
		t.Fatal("started again once closed")
	}
}

func TestRelayRefusesAWeakSecret(t *testing.T) {
	for _, secret := range []string{"", "short", "has/slash-0123456789", "has space 0123456789", strings.Repeat("a", 129)} {
		relay := &Relay{Dial: (&net.Dialer{}).DialContext, Secret: secret}
		if _, err := relay.Start(0); err == nil {
			relay.Close()
			t.Errorf("secret %q accepted", secret)
		}
	}
}
