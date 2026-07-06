package mitm

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"errors"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/url"
	"os"
	"runtime/debug"
	"strings"
	"sync"
	"time"

	utls "github.com/refraction-networking/utls"

	"github.com/moomok/zero-browser-tls-sidecar/fingerprint"
)

// CA holds the loaded Root CA cert + private key and a derived *tls.Config
// used to sign leaf certs on the fly.
type CA struct {
	Cert     *x509.Certificate
	CertDER  []byte
	Key      *rsa.PrivateKey
	mu       sync.Mutex
	issuedBy map[string]time.Time // host -> cert not-before, for backdating cache
}

// LoadCA reads a PEM-encoded Root CA cert + key from disk.
func LoadCA(certPath, keyPath string) (*CA, error) {
	certPEM, err := os.ReadFile(certPath)
	if err != nil {
		return nil, fmt.Errorf("read ca cert: %w", err)
	}
	keyPEM, err := os.ReadFile(keyPath)
	if err != nil {
		return nil, fmt.Errorf("read ca key: %w", err)
	}

	pair, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		return nil, fmt.Errorf("parse ca keypair: %w", err)
	}
	if pair.Leaf == nil {
		// X509KeyPair populates Leaf only when the cert is a single cert.
		// Fall back to parsing manually.
		leaf, err := x509.ParseCertificate(pair.Certificate[0])
		if err != nil {
			return nil, fmt.Errorf("parse ca leaf: %w", err)
		}
		pair.Leaf = leaf
	}

	keyPair, ok := pair.Leaf.PublicKey.(*rsa.PublicKey)
	if !ok {
		return nil, errors.New("CA public key is not RSA")
	}
	priv, ok := pair.PrivateKey.(*rsa.PrivateKey)
	if !ok {
		return nil, errors.New("CA private key is not RSA")
	}
	_ = keyPair // referenced via pair.Leaf.PublicKey indirectly

	return &CA{
		Cert:     pair.Leaf,
		CertDER:  pair.Certificate[0],
		Key:      priv,
		issuedBy: map[string]time.Time{},
	}, nil
}

// GetCertificate is a tls.Config.GetCertificate compatible function that signs
// a fresh leaf certificate for whatever SNI the client requested.
func (c *CA) GetCertificate(chi *tls.ClientHelloInfo) (*tls.Certificate, error) {
	host := chi.ServerName
	if host == "" {
		return nil, errors.New("no SNI provided by client")
	}
	leaf, err := c.signLeaf(host)
	if err != nil {
		return nil, err
	}
	return leaf, nil
}

func (c *CA) signLeaf(host string) (*tls.Certificate, error) {
	// Read cache status under lock, release before doing the (expensive)
	// serial/keypair generation. The cache is informational only — the
	// comment in the previous version said "Stale cache handled implicitly
	// by AlwaysExpireIn; return a fresh copy with current NotAfter but
	// reuse the keypair", but in practice the code ALWAYS generated a
	// fresh keypair and serial even on cache hit, so the cache is only
	// used to track when we last signed a leaf for a given host.
	//
	// Critical: do NOT acquire c.mu twice in this function in a way that
	// could double-Unlock. The previous implementation had a bug where the
	// cache-hit branch did `c.mu.Unlock()` then fell through to a second
	// `c.mu.Unlock()` at the bottom of the cache-check block, producing
	// `sync: unlock of unlocked mutex` — a runtime fatal (NOT a panic, so
	// recover() cannot catch it). That fatal crashed the sidecar with
	// exit code 2 on every CONNECT to a host it had seen before, which
	// manifested as Chromium getting ERR_PROXY_CONNECTION_FAILED on the
	// second navigation of a profile session.
	now := time.Now()
	c.mu.Lock()
	_, cacheHit := c.issuedBy[host]
	c.mu.Unlock()

	_ = cacheHit // reserved for future per-host keypair reuse

	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return nil, fmt.Errorf("serial: %w", err)
	}

	leafKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, fmt.Errorf("generate leaf key: %w", err)
	}

	template := &x509.Certificate{
		SerialNumber: serial,
		Subject: pkix.Name{
			CommonName:   host,
			Organization: []string{"ZeroBrowser TLS Sidecar"},
		},
		NotBefore: now.Add(-1 * time.Hour),
		NotAfter:  now.Add(24 * time.Hour),
		KeyUsage:  x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage: []x509.ExtKeyUsage{
			x509.ExtKeyUsageServerAuth,
		},
		DNSNames: []string{host},
	}

	if ip := net.ParseIP(host); ip != nil {
		template.DNSNames = nil
		template.IPAddresses = []net.IP{ip}
	}

	der, err := x509.CreateCertificate(rand.Reader, template, c.Cert, &leafKey.PublicKey, c.Key)
	if err != nil {
		return nil, fmt.Errorf("sign leaf: %w", err)
	}

	parsed, err := x509.ParseCertificate(der)
	if err != nil {
		return nil, fmt.Errorf("parse signed leaf: %w", err)
	}

	c.mu.Lock()
	c.issuedBy[host] = now
	c.mu.Unlock()

	return &tls.Certificate{
		Certificate: [][]byte{der, c.CertDER},
		PrivateKey:  leafKey,
		Leaf:        parsed,
	}, nil
}

// Handler is the HTTP CONNECT handler that performs the uTLS MITM bridge.
type Handler struct {
	CA            *CA
	Template      fingerprint.Template
	UpstreamProxy *url.URL
	Verbose       bool
	OnConnection  func(host string)
}

// HandleConnect implements the CONNECT method:
//  1. Read CONNECT target.
//  2. Spin up a TLS listener on an ephemeral port that serves leaf certs.
//  3. Hijack the client, do the native TLS handshake, then start uTLS dial
//     to the real target. Bidirectional pipe between the two.
//
// Panic recovery: each CONNECT runs in its own goroutine spawned by
// http.Server. Without recover() a panic in signLeaf, tls.Handshake, or the
// bidirectional pipe would CRASH THE ENTIRE SIDECAR PROCESS (in Go, an
// unrecovered panic in any goroutine exits the program with code 2). That
// would kill the sidecar mid-session and break every subsequent request
// from Chromium (ERR_PROXY_CONNECTION_FAILED). We wrap the body so panics
// are logged with stack trace and the bad connection is closed, while the
// sidecar keeps serving.
func (h *Handler) HandleConnect(w http.ResponseWriter, r *http.Request) {
	defer func() {
		if rec := recover(); rec != nil {
			host := r.Host
			if r.URL != nil {
				host = r.URL.Host
			}
			log.Printf("PANIC in HandleConnect host=%s: %v\n%s", host, rec, debug.Stack())
			// Try to write an HTTP error if we haven't hijacked yet.
			// After Hijack() succeeds, writing to w would corrupt the
			// hijacked socket, so we swallow that case.
			defer func() { _ = recover() }()
			func() {
				defer func() { _ = recover() }()
				http.Error(w, "sidecar: internal error", http.StatusInternalServerError)
			}()
		}
	}()

	host := r.URL.Host
	if host == "" {
		host = r.Host
	}
	if !strings.Contains(host, ":") {
		host += ":443"
	}
	targetHost, _, err := net.SplitHostPort(host)
	if err != nil {
		http.Error(w, "bad target host", http.StatusBadRequest)
		return
	}

	// Hijack the client connection so we can speak TLS to Chromium directly
	// using our leaf certificate. Chromium routes traffic through this proxy
	// on a single port, so we don't need a separate leaf listener — we just
	// upgrade the same socket in place.
	w.Header().Set("Content-Length", "0")
	w.WriteHeader(http.StatusOK)
	hijacker, ok := w.(http.Hijacker)
	if !ok {
		return
	}
	clientConn, _, err := hijacker.Hijack()
	if err != nil {
		return
	}
	defer clientConn.Close()

	tlsCfg := &tls.Config{
		GetCertificate: h.CA.GetCertificate,
		MinVersion:     tls.VersionTLS12,
	}

	// Native TLS handshake on the client side (Chromium) using our leaf cert.
	tlsConn := tls.Server(clientConn, tlsCfg)
	if err := tlsConn.Handshake(); err != nil {
		if h.Verbose {
			fmt.Fprintln(os.Stderr, "leaf handshake:", err)
		}
		return
	}
	defer tlsConn.Close()

	// Dial target with uTLS. Pass host (with port) for the dial and
	// targetHost (hostname only) for SNI.
	var targetConn net.Conn
	if h.UpstreamProxy != nil {
		targetConn, err = dialViaUpstreamProxy(h.UpstreamProxy, host, h.Template)
	} else {
		targetConn, err = dialUTLS("tcp", host, h.Template)
	}
	if err != nil {
		if h.Verbose {
			fmt.Fprintln(os.Stderr, "utls dial:", err)
		}
		return
	}
	defer targetConn.Close()

	if h.OnConnection != nil {
		h.OnConnection(targetHost)
	}

	// Bidirectional pipe.
	done := make(chan struct{}, 2)
	go func() { _, _ = copyAndClose(tlsConn, targetConn); done <- struct{}{} }()
	go func() { _, _ = copyAndClose(targetConn, tlsConn); done <- struct{}{} }()
	<-done
}

func copyAndClose(dst, src net.Conn) (int64, error) {
	defer src.Close()
	n, err := copyBytes(dst, src)
	return n, err
}

func copyBytes(dst io.Writer, src io.Reader) (int64, error) {
	buf := make([]byte, 32*1024)
	var total int64
	for {
		n, rerr := src.Read(buf)
		if n > 0 {
			if _, werr := dst.Write(buf[:n]); werr != nil {
				return total, werr
			}
			total += int64(n)
		}
		if rerr != nil {
			return total, rerr
		}
	}
}

func dialUTLS(network, host string, tpl fingerprint.Template) (net.Conn, error) {
	rawConn, err := net.Dial(network, host)
	if err != nil {
		return nil, err
	}
	tlsCfg := &utls.Config{
		ServerName:         hostOnly(host),
		InsecureSkipVerify: true, // we don't validate upstream certs in this proxy
	}
	uconn := utls.UClient(rawConn, tlsCfg, tpl.ClientHello)
	if err := uconn.Handshake(); err != nil {
		rawConn.Close()
		return nil, err
	}
	return uconn, nil
}

func dialViaUpstreamProxy(proxy *url.URL, targetHost string, tpl fingerprint.Template) (net.Conn, error) {
	var proxyConn net.Conn
	var err error
	switch proxy.Scheme {
	case "socks5":
		proxyConn, err = dialSocks5(proxy, hostOnlyPort(targetHost))
		if err != nil {
			return nil, err
		}
		// Now do uTLS on top of the SOCKS5 tunnel.
		rawConn := proxyConn
		tlsCfg := &utls.Config{
			ServerName:         hostOnly(targetHost),
			InsecureSkipVerify: true,
		}
		uconn := utls.UClient(rawConn, tlsCfg, tpl.ClientHello)
		if err := uconn.Handshake(); err != nil {
			rawConn.Close()
			return nil, err
		}
		return uconn, nil
	default:
		// HTTP CONNECT through upstream proxy.
		proxyConn, err = net.Dial("tcp", proxy.Host)
		if err != nil {
			return nil, err
		}
		connectReq := []string{
			fmt.Sprintf("CONNECT %s HTTP/1.1", targetHost),
			fmt.Sprintf("Host: %s", targetHost),
			"Proxy-Connection: keep-alive",
			"",
			"",
		}
		if proxy.User != nil {
			pwd, _ := proxy.User.Password()
			auth := basicAuth(proxy.User.Username(), pwd)
			connectReq = append(connectReq[:3], append([]string{fmt.Sprintf("Proxy-Authorization: %s", auth)}, connectReq[3:]...)...)
		}
		if _, err := proxyConn.Write([]byte(strings.Join(connectReq, "\r\n"))); err != nil {
			proxyConn.Close()
			return nil, err
		}
		// Read 200 response.
		buf := make([]byte, 4096)
		n, err := proxyConn.Read(buf)
		if err != nil || n < 12 || !strings.HasPrefix(string(buf[:n]), "HTTP/1.1 200") {
			proxyConn.Close()
			return nil, fmt.Errorf("upstream CONNECT failed: %s", string(buf[:min(n, 200)]))
		}

		tlsCfg := &utls.Config{
			ServerName:         hostOnly(targetHost),
			InsecureSkipVerify: true,
		}
		uconn := utls.UClient(proxyConn, tlsCfg, tpl.ClientHello)
		if err := uconn.Handshake(); err != nil {
			proxyConn.Close()
			return nil, err
		}
		return uconn, nil
	}
}

// Minimal SOCKS5 dial supporting username/password auth (RFC 1929).
// Avoids pulling in a full SOCKS5 library for one feature.
func dialSocks5(proxy *url.URL, target string) (net.Conn, error) {
	conn, err := net.Dial("tcp", proxy.Host)
	if err != nil {
		return nil, err
	}
	// Greeting: version + method count + methods.
	user := ""
	pass := ""
	if proxy.User != nil {
		user = proxy.User.Username()
		if p, ok := proxy.User.Password(); ok {
			pass = p
		}
	}
	methods := []byte{0x00} // no-auth
	if user != "" {
		methods = []byte{0x02} // user/pass
	}
	if _, err := conn.Write([]byte{0x05, byte(len(methods))}); err != nil {
		conn.Close()
		return nil, err
	}
	if _, err := conn.Write(methods); err != nil {
		conn.Close()
		return nil, err
	}
	// Server choice.
	hdr := make([]byte, 2)
	if _, err := conn.Read(hdr); err != nil {
		conn.Close()
		return nil, err
	}
	if hdr[0] != 0x05 {
		conn.Close()
		return nil, errors.New("socks5: bad version")
	}
	switch hdr[1] {
	case 0x00:
		// no-auth
	case 0x02:
		if user == "" {
			conn.Close()
			return nil, errors.New("socks5: server requested auth, no creds provided")
		}
		authReq := []byte{0x01, byte(len(user))}
		authReq = append(authReq, user...)
		authReq = append(authReq, byte(len(pass)))
		authReq = append(authReq, pass...)
		if _, err := conn.Write(authReq); err != nil {
			conn.Close()
			return nil, err
		}
		resp := make([]byte, 2)
		if _, err := conn.Read(resp); err != nil {
			conn.Close()
			return nil, err
		}
		if resp[1] != 0x00 {
			conn.Close()
			return nil, fmt.Errorf("socks5: auth failed (status=%d)", resp[1])
		}
	default:
		conn.Close()
		return nil, fmt.Errorf("socks5: unsupported method %d", hdr[1])
	}
	// Connect request.
	hostPart, portPart, err := net.SplitHostPort(target)
	if err != nil {
		conn.Close()
		return nil, err
	}
	portNum, err := parsePort(portPart)
	if err != nil {
		conn.Close()
		return nil, err
	}
	req := []byte{0x05, 0x01, 0x00}
	if ip := net.ParseIP(hostPart); ip != nil {
		if ip4 := ip.To4(); ip4 != nil {
			req = append(req, 0x01)
			req = append(req, ip4...)
		} else {
			req = append(req, 0x04)
			req = append(req, ip...)
		}
	} else {
		req = append(req, 0x03)
		req = append(req, byte(len(hostPart)))
		req = append(req, hostPart...)
	}
	portBytes := make([]byte, 2)
	portBytes[0] = byte(portNum >> 8)
	portBytes[1] = byte(portNum & 0xff)
	req = append(req, portBytes...)
	if _, err := conn.Write(req); err != nil {
		conn.Close()
		return nil, err
	}
	respHdr := make([]byte, 4)
	if _, err := readFull(conn, respHdr); err != nil {
		conn.Close()
		return nil, err
	}
	if respHdr[0] != 0x05 || respHdr[1] != 0x00 {
		conn.Close()
		return nil, fmt.Errorf("socks5: connect failed (rep=%d)", respHdr[1])
	}
	// Skip the BND.ADDR/BND.PORT fields by reading whatever the server returns.
	// We don't actually need the bound address.
	switch respHdr[3] {
	case 0x01:
		_, _ = readFull(conn, make([]byte, 4+2))
	case 0x03:
		lbuf := make([]byte, 1)
		_, _ = conn.Read(lbuf)
		_, _ = readFull(conn, make([]byte, int(lbuf[0])+2))
	case 0x04:
		_, _ = readFull(conn, make([]byte, 16+2))
	}
	return conn, nil
}

func readFull(conn net.Conn, buf []byte) (int, error) {
	total := 0
	for total < len(buf) {
		n, err := conn.Read(buf[total:])
		if n > 0 {
			total += n
		}
		if err != nil {
			return total, err
		}
	}
	return total, nil
}

func parsePort(s string) (int, error) {
	var p int
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, errors.New("invalid port")
		}
		p = p*10 + int(c-'0')
	}
	if p <= 0 || p > 65535 {
		return 0, errors.New("port out of range")
	}
	return p, nil
}

func hostOnly(h string) string {
	if i := strings.LastIndex(h, ":"); i >= 0 {
		return h[:i]
	}
	return h
}

func hostOnlyPort(h string) string {
	return h
}

func basicAuth(user, pass string) string {
	const encode = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
	raw := user + ":" + pass
	var out strings.Builder
	i := 0
	for ; i+2 < len(raw); i += 3 {
		v := uint32(raw[i])<<16 | uint32(raw[i+1])<<8 | uint32(raw[i+2])
		out.WriteByte(encode[(v>>18)&0x3f])
		out.WriteByte(encode[(v>>12)&0x3f])
		out.WriteByte(encode[(v>>6)&0x3f])
		out.WriteByte(encode[v&0x3f])
	}
	if i < len(raw) {
		v := uint32(raw[i]) << 16
		if i+1 < len(raw) {
			v |= uint32(raw[i+1]) << 8
		}
		out.WriteByte(encode[(v>>18)&0x3f])
		out.WriteByte(encode[(v>>12)&0x3f])
		if i+1 < len(raw) {
			out.WriteByte(encode[(v>>6)&0x3f])
		} else {
			out.WriteByte('=')
		}
		out.WriteByte('=')
	}
	return out.String()
}
