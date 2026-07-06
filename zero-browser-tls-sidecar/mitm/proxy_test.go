// Multi-request lifecycle test. The bug we're guarding against: sidecar
// process exits after handling one CONNECT request, which makes every
// subsequent Chromium navigation fail with ERR_PROXY_CONNECTION_FAILED.
//
// Root cause when this test was added: in early v0.4 the sidecar had no
// panic recovery. A panic in one request goroutine (signLeaf, tls.Handshake,
// the bidirectional pipe) would crash the entire Go process with exit code 2
// — Go's behavior is "panic in any goroutine = process crash", unlike .NET
// where exceptions are scoped to the throwing thread.
//
// This test verifies that after handling N CONNECTs (sequential, parallel,
// and a mixed Chromium-like pattern) the sidecar is still serving — i.e.
// the listener accepts new connections and /health responds.
//
// Run with:
//   go test -count=1 ./mitm/...
package mitm

import (
	"crypto/tls"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

const (
	sidecarBinaryName = "zero-browser-tls-sidecar"
)

// findSidecar locates the sidecar binary in the workspace. Tests run from
// the mitm package directory (mitm/), so we look up two levels then into
// vendor/tls-sidecar/.
func findSidecar(t *testing.T) string {
	t.Helper()
	runtimeGOOS := runtime.GOOS
	runtimeGOARCH := runtime.GOARCH
	exeSuffix := ""
	if runtimeGOOS == "windows" {
		exeSuffix = ".exe"
	}
	rel := filepath.Join("..", "..", "vendor", "tls-sidecar",
		fmt.Sprintf("%s-%s", runtimeGOOS, runtimeGOARCH),
		sidecarBinaryName+exeSuffix)
	abs, err := filepath.Abs(rel)
	if err != nil {
		t.Fatalf("abs path: %v", err)
	}
	if _, err := os.Stat(abs); err != nil {
		t.Skipf("sidecar binary not built at %s (run build.ps1 first): %v", abs, err)
	}
	return abs
}

// findTestCA returns the test CA from zero-browser-tls-sidecar/test-ca/.
// Generated once by running: openssl req -x509 -newkey rsa:2048 -nodes
// -keyout test-ca.key.pem -out test-ca.crt -days 3650 -subj '/CN=zb-test'
func findTestCA(t *testing.T) (certPath, keyPath string) {
	t.Helper()
	base, err := filepath.Abs(filepath.Join("..", "..", "zero-browser-tls-sidecar", "test-ca"))
	if err != nil {
		t.Fatalf("abs: %v", err)
	}
	certPath = filepath.Join(base, "test-ca.crt")
	keyPath = filepath.Join(base, "test-ca.key.pem")
	if _, err := os.Stat(certPath); err != nil {
		t.Skipf("test CA not found at %s: %v", certPath, err)
	}
	if _, err := os.Stat(keyPath); err != nil {
		t.Skipf("test CA key not found at %s: %v", keyPath, err)
	}
	return
}

// findFreePort asks the OS for a free TCP port. There is a TOCTOU race
// between Close and the sidecar's Listen, but in practice it's fine —
// port 0 means "pick one" and we close immediately so the kernel can
// re-assign.
func findFreePort(t *testing.T) int {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port
}

// sidecarProcess is a small helper that starts the sidecar binary, waits for
// /health to become reachable, and tracks the cmd so tests can defer kill.
type sidecarProcess struct {
	cmd      *exec.Cmd
	port     int
	healthOK chan struct{}
	t        *testing.T
}

func startSidecar(t *testing.T, sidecarPath, caCert, caKey string, port int) *sidecarProcess {
	t.Helper()
	sp := &sidecarProcess{
		port:     port,
		healthOK: make(chan struct{}),
		t:        t,
	}
	sp.cmd = exec.Command(sidecarPath,
		fmt.Sprintf("--port=%d", port),
		"--profile-id=lifecycle-test",
		"--fingerprint-mode=chrome",
		"--seed=hello",
		"--ca-cert="+caCert,
		"--ca-key="+caKey,
	)
	// Capture stderr so test failure shows the sidecar's panic traces
	// (when we don't recover). Tests asserting "sidecar stays alive" can
	// ignore this; tests asserting "panic was recovered" inspect it.
	sp.cmd.Stderr = os.Stderr

	if err := sp.cmd.Start(); err != nil {
		t.Fatalf("start sidecar: %v", err)
	}
	t.Cleanup(func() {
		if runtime.GOOS == "windows" {
			_ = sp.cmd.Process.Kill()
		} else {
			_ = sp.cmd.Process.Signal(os.Interrupt)
		}
		_, _ = sp.cmd.Process.Wait()
	})

	// Poll /health on port+1.
	go func() {
		deadline := time.Now().Add(10 * time.Second)
		for time.Now().Before(deadline) {
			conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port+1), 500*time.Millisecond)
			if err == nil {
				conn.Close()
				close(sp.healthOK)
				return
			}
			time.Sleep(100 * time.Millisecond)
		}
	}()
	select {
	case <-sp.healthOK:
		return sp
	case <-time.After(10 * time.Second):
		t.Fatalf("sidecar never became healthy on port %d", port+1)
		return nil
	}
}

func (sp *sidecarProcess) isAlive() bool {
	if sp.cmd.ProcessState != nil && sp.cmd.ProcessState.Exited() {
		return false
	}
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", sp.port+1), 500*time.Millisecond)
	if err != nil {
		return false
	}
	conn.Close()
	return true
}

// sendOneConnect fires a single CONNECT-then-TLS-handshake against the
// sidecar. Returns nil if the sidecar accepted and completed a leaf TLS
// handshake; an error otherwise. Used to count successful requests and to
// verify the sidecar doesn't bail out mid-session.
func sendOneConnect(sidecarPort int, host string) error {
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", sidecarPort), 5*time.Second)
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer conn.Close()

	if _, err := conn.Write([]byte(fmt.Sprintf("CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n", host, host))); err != nil {
		return fmt.Errorf("write CONNECT: %w", err)
	}

	conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		return fmt.Errorf("read CONNECT resp: %w", err)
	}
	if !strings.Contains(string(buf[:n]), "200") {
		return fmt.Errorf("CONNECT not 200: %s", strings.SplitN(string(buf[:n]), "\r\n", 2)[0])
	}

	tlsConn := tls.Client(conn, &tls.Config{
		ServerName:         host,
		InsecureSkipVerify: true,
	})
	if err := tlsConn.Handshake(); err != nil {
		return fmt.Errorf("leaf handshake: %w", err)
	}
	defer tlsConn.Close()

	// Make the GET so the bidirectional pipe actually runs end-to-end.
	if _, err := tlsConn.Write([]byte(fmt.Sprintf("GET / HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n", host))); err != nil {
		return fmt.Errorf("write GET: %w", err)
	}
	tlsConn.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, _ = io.Copy(io.Discard, tlsConn)
	return nil
}

func TestSidecar_SurvivesMultipleSequentialConnects(t *testing.T) {
	sidecarPath := findSidecar(t)
	caCert, caKey := findTestCA(t)
	port := findFreePort(t)
	sp := startSidecar(t, sidecarPath, caCert, caKey, port)

	hosts := []string{"tls.peet.ws", "example.com", "www.google.com", "github.com", "cloudflare.com"}
	for _, h := range hosts {
		if err := sendOneConnect(port, h); err != nil {
			t.Fatalf("sequential CONNECT to %s failed: %v (sidecar alive=%v)", h, err, sp.isAlive())
		}
	}
	if !sp.isAlive() {
		t.Fatal("sidecar died after 5 sequential CONNECTs — this is the bug we're guarding against")
	}
}

func TestSidecar_SurvivesMultipleParallelConnects(t *testing.T) {
	// Chromium with --proxy-server may open several connections concurrently
	// (HTML, favicon, preconnect, speculative DNS). Verify the sidecar
	// tolerates concurrent CONNECTs without dying.
	sidecarPath := findSidecar(t)
	caCert, caKey := findTestCA(t)
	port := findFreePort(t)
	sp := startSidecar(t, sidecarPath, caCert, caKey, port)

	hosts := []string{"tls.peet.ws", "example.com", "www.google.com", "github.com", "cloudflare.com"}
	var wg sync.WaitGroup
	errs := make(chan error, len(hosts))
	for _, h := range hosts {
		h := h
		wg.Add(1)
		go func() {
			defer wg.Done()
			errs <- sendOneConnect(port, h)
		}()
	}
	wg.Wait()
	close(errs)
	for e := range errs {
		if e != nil {
			t.Errorf("parallel CONNECT failed: %v (sidecar alive=%v)", e, sp.isAlive())
		}
	}
	if !sp.isAlive() {
		t.Fatal("sidecar died after parallel CONNECT burst")
	}
}

func TestSidecar_SurvivesMixedPatternLikeChromium(t *testing.T) {
	// Chromium's typical page-load pattern: 1 main HTML load, then a burst
	// of subresource fetches (favicon, scripts, images, fonts). Simulate
	// with one sequential CONNECT + 20 parallel CONNECTs to the same host.
	sidecarPath := findSidecar(t)
	caCert, caKey := findTestCA(t)
	port := findFreePort(t)
	sp := startSidecar(t, sidecarPath, caCert, caKey, port)

	if err := sendOneConnect(port, "example.com"); err != nil {
		t.Fatalf("phase 1 sequential: %v", err)
	}
	var wg sync.WaitGroup
	const N = 20
	for i := 0; i < N; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_ = sendOneConnect(port, "example.com")
		}()
	}
	wg.Wait()
	if !sp.isAlive() {
		t.Fatal("sidecar died after mixed sequential+burst pattern")
	}
}

// Silence unused import warnings when only some tests reference these.
var _ = http.MethodConnect
