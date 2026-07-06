// verify-via-server connects to a running sidecar instance as an HTTP CONNECT
// proxy, fetches https://tls.peet.ws/api/all through it, and prints the JA3
// hash that tls.peet.ws saw on the LEG B connection (which is the one
// produced by uTLS inside the sidecar).
//
// Usage:
//   # Terminal 1:
//   ./zero-browser-tls-sidecar --port 39300 --profile-id test \
//     --fingerprint-mode chrome --seed profile-A \
//     --ca-cert ./test-ca/test-ca.crt --ca-key ./test-ca/test-ca.key.pem
//
//   # Terminal 2:
//   ./verify-via-server --sidecar-port 39300

package main

import (
	"crypto/tls"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"
)

func main() {
	sidecarPort := flag.Int("sidecar-port", 39300, "port the sidecar is listening on")
	targetHost := flag.String("target", "tls.peet.ws", "host to fetch JA3 from")
	targetPath := flag.String("path", "/api/all", "path")
	flag.Parse()

	// Open raw TCP to sidecar.
	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", *sidecarPort))
	if err != nil {
		log.Fatalf("dial sidecar: %v", err)
	}
	defer conn.Close()

	// Send CONNECT to sidecar.
	connectReq := fmt.Sprintf("CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n", *targetHost, *targetHost)
	if _, err := conn.Write([]byte(connectReq)); err != nil {
		log.Fatalf("write CONNECT: %v", err)
	}

	// Read CONNECT response.
	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		log.Fatalf("read CONNECT response: %v", err)
	}
	respLine := string(buf[:n])
	if !strings.Contains(respLine, "200") {
		log.Fatalf("CONNECT failed: %s", strings.SplitN(respLine, "\r\n", 2)[0])
	}
	fmt.Fprintf(os.Stderr, "CONNECT established\n")

	// Upgrade the raw conn to TLS using leaf cert (sidecar's MITM). We skip
	// verify because we don't have the test CA in our trust store and don't
	// care — we're verifying the LEG B uTLS fingerprint, not Leg A.
	tlsConn := tls.Client(conn, &tls.Config{
		ServerName:         *targetHost,
		InsecureSkipVerify: true,
	})
	if err := tlsConn.Handshake(); err != nil {
		log.Fatalf("leaf TLS handshake: %v", err)
	}
	defer tlsConn.Close()
	fmt.Fprintf(os.Stderr, "leaf TLS handshake OK; peer certs=%d\n", len(tlsConn.ConnectionState().PeerCertificates))

	// Send HTTP GET.
	req := fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: Mozilla/5.0 ZeroBrowserVerify\r\nConnection: close\r\n\r\n", *targetPath, *targetHost)
	if _, err := tlsConn.Write([]byte(req)); err != nil {
		log.Fatalf("write GET: %v", err)
	}
	fmt.Fprintf(os.Stderr, "GET sent, waiting for response (timeout 15s)...\n")
	if err := tlsConn.SetReadDeadline(time.Now().Add(15 * time.Second)); err != nil {
		log.Fatalf("set deadline: %v", err)
	}

	// Read full response.
	body, err := io.ReadAll(tlsConn)
	if err != nil {
		log.Fatalf("read response (%d bytes): %v", len(body), err)
	}
	fmt.Fprintf(os.Stderr, "response received: %d bytes\n", len(body))

	// Split headers and body.
	parts := strings.SplitN(string(body), "\r\n\r\n", 2)
	if len(parts) != 2 {
		log.Fatalf("malformed response (len=%d)", len(body))
	}
	var bodyJSON map[string]any
	if err := json.Unmarshal([]byte(parts[1]), &bodyJSON); err != nil {
		log.Fatalf("parse JSON: %v\nbody: %s", err, parts[1])
	}

	tlsInfo, _ := bodyJSON["tls"].(map[string]any)
	if tlsInfo == nil {
		log.Fatalf("no tls key in response: %v", bodyJSON)
	}

	fmt.Println("---")
	fmt.Println("server-reported JA3 =", tlsInfo["ja3"])
	fmt.Println("server-reported JA3_hash =", tlsInfo["ja3_hash"])
	fmt.Println("server-reported tls_version_record =", tlsInfo["tls_version_record"])
	fmt.Println("server-reported tls_version_negotiated =", tlsInfo["tls_version_negotiated"])
	fmt.Println("---")
	if u, err := url.Parse("https://ja3er.com/search/" + asString(tlsInfo["ja3_hash"])); err == nil {
		fmt.Println("lookup URL: https://ja3er.com/search/" + asString(tlsInfo["ja3_hash"]))
		_ = u
	}
}

// Use http.Client for a moment to satisfy unused import in case we extend later.
var _ = http.DefaultClient

func asString(v any) string {
	if s, ok := v.(string); ok {
		return s
	}
	return fmt.Sprintf("%v", v)
}

// silence unused time import if no deadline is used
var _ = time.Second
