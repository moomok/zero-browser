// Multi-request lifecycle test for the sidecar. Spawns the sidecar binary,
// sends N CONNECT requests to different hosts in sequence, verifies the
// sidecar stays alive across all of them.
//
// Usage:
//   go run ./cmd/multi-request-lifecycle --sidecar=path/to/exe
package main

import (
	"bytes"
	"crypto/tls"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

func main() {
	sidecarPath := flag.String("sidecar", "", "path to zero-browser-tls-sidecar binary")
	caCert := flag.String("ca-cert", "", "path to PEM-encoded Root CA cert")
	caKey := flag.String("ca-key", "", "path to PEM-encoded Root CA private key")
	port := flag.Int("port", 39500, "port for sidecar to listen on")
	hosts := flag.String("hosts", "tls.peet.ws,example.com,www.google.com,github.com,cloudflare.com", "comma-separated hosts to test")
	mode := flag.String("mode", "sequential", "sequential | parallel | mixed (sequential then 30 parallel)")
	flag.Parse()

	if *sidecarPath == "" || *caCert == "" || *caKey == "" {
		fmt.Fprintln(os.Stderr, "--sidecar, --ca-cert, --ca-key are required")
		os.Exit(2)
	}

	// Spawn sidecar.
	fmt.Printf("[1/4] Spawning sidecar at port %d...\n", *port)
	cmd := exec.Command(*sidecarPath,
		fmt.Sprintf("--port=%d", *port),
		"--profile-id=lifecycle-test",
		"--fingerprint-mode=chrome",
		"--seed=hello",
		fmt.Sprintf("--ca-cert=%s", *caCert),
		fmt.Sprintf("--ca-key=%s", *caKey),
	)
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		fmt.Fprintf(os.Stderr, "failed to start sidecar: %v\n", err)
		os.Exit(2)
	}
	defer func() {
		if runtime.GOOS == "windows" {
			_ = cmd.Process.Kill()
		} else {
			_ = cmd.Process.Signal(os.Interrupt)
		}
		_, _ = cmd.Process.Wait()
	}()

	// Wait for /health on port+1.
	fmt.Println("[2/4] Waiting for sidecar health endpoint...")
	healthAddr := fmt.Sprintf("127.0.0.1:%d", *port+1)
	if err := waitHealth(healthAddr, 10*time.Second); err != nil {
		fmt.Fprintf(os.Stderr, "sidecar never became healthy: %v\n", err)
		os.Exit(3)
	}
	fmt.Println("      sidecar is ready")

	hostList := strings.Split(*hosts, ",")
	fmt.Printf("[3/4] Sending CONNECT (mode=%s) to %d hosts...\n", *mode, len(hostList))

	results := make([]string, 0, len(hostList))
	switch *mode {
	case "sequential":
		for _, host := range hostList {
			host = strings.TrimSpace(host)
			ok, detail := sendConnect(*port, host)
			if !ok {
				alive := cmd.ProcessState == nil || !cmd.ProcessState.Exited()
				results = append(results, fmt.Sprintf("  %-25s FAIL  %s  (sidecar alive=%v)", host, detail, alive))
				if !alive {
					fmt.Println("  *** SIDECAR CRASHED — aborting test ***")
					for _, r := range results {
						fmt.Println(r)
					}
					os.Exit(4)
				}
			} else {
				results = append(results, fmt.Sprintf("  %-25s OK    %s", host, detail))
			}
		}
	case "parallel":
		type result struct {
			host string
			ok   bool
			det  string
		}
		ch := make(chan result, len(hostList))
		for _, h := range hostList {
			h = strings.TrimSpace(h)
			go func() {
				ok, det := sendConnect(*port, h)
				ch <- result{h, ok, det}
			}()
		}
		for range hostList {
			r := <-ch
			if !r.ok {
				alive := cmd.ProcessState == nil || !cmd.ProcessState.Exited()
				results = append(results, fmt.Sprintf("  %-25s FAIL  %s  (sidecar alive=%v)", r.host, r.det, alive))
			} else {
				results = append(results, fmt.Sprintf("  %-25s OK    %s", r.host, r.det))
			}
		}
		// Check if sidecar crashed mid-flight
		if cmd.ProcessState != nil && cmd.ProcessState.Exited() {
			fmt.Println("  *** SIDECAR CRASHED during parallel CONNECTs ***")
			for _, r := range results {
				fmt.Println(r)
			}
			os.Exit(4)
		}
	case "mixed":
		// Sequential then a burst of parallel — what Chromium typically does:
		// 1 HTML load, then ~10 parallel subresource fetches.
		fmt.Println("      phase 1: sequential (simulates main HTML)")
		for _, h := range hostList {
			h = strings.TrimSpace(h)
			ok, det := sendConnect(*port, h)
			results = append(results, fmt.Sprintf("  [seq] %-19s %s  %s", h, map[bool]string{true: "OK", false: "FAIL"}[ok], det))
			if !ok && (cmd.ProcessState != nil && cmd.ProcessState.Exited()) {
				fmt.Println("  *** SIDECAR CRASHED during sequential phase ***")
				os.Exit(4)
			}
		}
		fmt.Println("      phase 2: 30 parallel CONNECTs to first host (simulates favicon/spam)")
		type result struct{ ok bool; det string }
		ch := make(chan result, 30)
		for i := 0; i < 30; i++ {
			go func() {
				ok, det := sendConnect(*port, hostList[0])
				ch <- result{ok, det}
			}()
		}
		okCount := 0
		for i := 0; i < 30; i++ {
			r := <-ch
			if r.ok {
				okCount++
			}
		}
		results = append(results, fmt.Sprintf("  [par] 30x %-19s %d/30 OK", hostList[0], okCount))
		if cmd.ProcessState != nil && cmd.ProcessState.Exited() {
			fmt.Println("  *** SIDECAR CRASHED during parallel phase ***")
			for _, r := range results {
				fmt.Println(r)
			}
			os.Exit(4)
		}
	}

	fmt.Println("      per-request results:")
	for _, r := range results {
		fmt.Println(r)
	}

	// Final health check.
	fmt.Println("[4/4] Verifying sidecar is still alive after all requests...")
	if cmd.ProcessState != nil && cmd.ProcessState.Exited() {
		fmt.Println("      FAIL — sidecar exited (likely panicked) during the run")
		os.Exit(5)
	}
	alive, err := healthAlive(healthAddr)
	if err != nil || !alive {
		fmt.Printf("      FAIL — health endpoint unreachable: alive=%v err=%v\n", alive, err)
		os.Exit(6)
	}
	fmt.Println("      OK — sidecar still healthy after", len(hostList), "requests")
	fmt.Println()
	fmt.Println("=== PASS: sidecar handles multiple sequential CONNECTs without dying ===")
}

func waitHealth(url string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		conn, err := net.DialTimeout("tcp", strings.TrimPrefix(url, "http://"), 500*time.Millisecond)
		if err == nil {
			conn.Close()
			// best-effort GET; ok if it 404s as long as port is listening
			return nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Errorf("timed out after %v", timeout)
}

func healthAlive(url string) (bool, error) {
	conn, err := net.DialTimeout("tcp", strings.TrimPrefix(url, "http://"), 1*time.Second)
	if err != nil {
		return false, err
	}
	conn.Close()
	return true, nil
}

func sendConnect(proxyPort int, host string) (bool, string) {
	// 1. Open TCP to sidecar.
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", proxyPort), 5*time.Second)
	if err != nil {
		return false, fmt.Sprintf("dial sidecar: %v", err)
	}
	defer conn.Close()

	// 2. Send CONNECT.
	req := fmt.Sprintf("CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n", host, host)
	if _, err := conn.Write([]byte(req)); err != nil {
		return false, fmt.Sprintf("write CONNECT: %v", err)
	}

	// 3. Read CONNECT response.
	conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		return false, fmt.Sprintf("read CONNECT resp: %v", err)
	}
	resp := string(buf[:n])
	if !strings.Contains(resp, "200") {
		return false, fmt.Sprintf("CONNECT failed: %s", strings.SplitN(resp, "\r\n", 2)[0])
	}

	// 4. Upgrade to TLS using our leaf cert. Skip verify — we don't have the
	//    test CA in our trust store and don't care; we're verifying the sidecar
	//    process lifecycle, not the upstream cert chain.
	tlsConn := tls.Client(conn, &tls.Config{
		ServerName:         host,
		InsecureSkipVerify: true,
	})
	if err := tlsConn.Handshake(); err != nil {
		return false, fmt.Sprintf("leaf TLS handshake: %v", err)
	}
	defer tlsConn.Close()

	// 5. Send a minimal HTTP GET and read a tiny bit of response so the
	//    bidirectional pipe actually runs (otherwise the request finishes
	//    before either copyAndClose goroutine has a chance to fire).
	req2 := fmt.Sprintf("GET / HTTP/1.0\r\nHost: %s\r\nUser-Agent: lifecycle-test\r\nConnection: close\r\n\r\n", host)
	if _, err := tlsConn.Write([]byte(req2)); err != nil {
		return false, fmt.Sprintf("write GET: %v", err)
	}
	tlsConn.SetReadDeadline(time.Now().Add(5 * time.Second))
	body := make([]byte, 256)
	_, _ = tlsConn.Read(body)
	return true, fmt.Sprintf("first-byte=%q", bytes.TrimSpace(body)[:min(40, len(bytes.TrimSpace(body)))])
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}

var _ = io.EOF
var _ = filepath.Separator
