// Tiny utility: connect to a host via the same uTLS preset the sidecar uses
// and print the JA3 hash of the ClientHello. This lets us verify empirically
// which JA3 our templates produce without needing to wire up Chromium.
//
// Usage: go run ./compute-ja3 --mode chrome --host tls.peet.ws:443
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"strings"

	utls "github.com/refraction-networking/utls"

	fp "github.com/moomok/zero-browser-tls-sidecar/fingerprint"
)

func main() {
	mode := flag.String("mode", "chrome", "chrome|firefox|safari|edge|randomized")
	seed := flag.String("seed", "verify-seed", "deterministic seed for randomized mode")
	host := flag.String("host", "tls.peet.ws:443", "host:port to dial")
	flag.Parse()

	tpl, err := fp.Select(*seed, *mode)
	if err != nil {
		log.Fatal(err)
	}
	fmt.Fprintf(os.Stderr, "template: %s\n", tpl.ID)

	rawConn, err := net.Dial("tcp", *host)
	if err != nil {
		log.Fatal(err)
	}
	defer rawConn.Close()

	tlsCfg := &utls.Config{ServerName: strings.Split(*host, ":")[0], InsecureSkipVerify: true}
	uconn := utls.UClient(rawConn, tlsCfg, tpl.ClientHello)
	if err := uconn.Handshake(); err != nil {
		log.Fatalf("handshake: %v", err)
	}

	// uTLS exposes the marshalled ClientHello via UConn.HandshakeState.Hello.
	hello := uconn.HandshakeState.Hello
	if hello == nil {
		log.Fatal("no ClientHello available in handshake state")
	}
	raw := hello.Raw
	fmt.Fprintf(os.Stderr, "raw len: %d\n", len(raw))
	if len(raw) == 0 {
		// Some older presets don't populate Raw. Try re-marshaling.
		var err error
		raw, err = hello.Marshal()
		if err != nil {
			log.Fatalf("marshal: %v", err)
		}
		fmt.Fprintf(os.Stderr, "marshal len: %d\n", len(raw))
	}
	if len(raw) == 0 {
		log.Fatal("no raw ClientHello bytes")
	}

	// Compute JA3 manually (the de facto format).
	ja3 := computeJA3(raw)
	fmt.Println("JA3 =", ja3)
}

func computeJA3(raw []byte) string {
	// Parse TLS record header: type(1) + version(2) + length(2) + handshake(4) + version(2) + random(32)
	if len(raw) < 5+4+2+32 {
		return ""
	}
	off := 5 + 4 + 2 + 32
	// session_id
	if off >= len(raw) {
		return ""
	}
	sidLen := int(raw[off])
	off += 1 + sidLen
	// cipher_suites
	if off+2 > len(raw) {
		return ""
	}
	csLen := int(uint16(raw[off])<<8 | uint16(raw[off+1]))
	off += 2
	if off+csLen > len(raw) {
		return ""
	}
	var ciphers []string
	for i := 0; i+2 <= csLen; i += 2 {
		c := uint16(raw[off+i])<<8 | uint16(raw[off+i+1])
		if c != 0x0a0a { // strip GREASE
			ciphers = append(ciphers, fmt.Sprintf("%d", c))
		}
	}
	off += csLen
	// compression_methods
	if off >= len(raw) {
		return ""
	}
	compLen := int(raw[off])
	off += 1
	if off+compLen > len(raw) {
		return ""
	}
	off += compLen
	// extensions
	if off+2 > len(raw) {
		return ""
	}
	extLen := int(uint16(raw[off])<<8 | uint16(raw[off+1]))
	off += 2
	end := off + extLen
	if end > len(raw) {
		end = len(raw)
	}
	var extIDs []string
	var curves []string
	var pointFmts []string
	for off+4 <= end {
		eid := uint16(raw[off])<<8 | uint16(raw[off+1])
		elen := int(uint16(raw[off+2])<<8 | uint16(raw[off+3]))
		off += 4
		if off+elen > end {
			break
		}
		if eid != 0x0a0a {
			extIDs = append(extIDs, fmt.Sprintf("%d", eid))
		}
		switch eid {
		case 0x000a: // supported_groups
			if elen >= 2 {
				l := int(uint16(raw[off])<<8 | uint16(raw[off+1]))
				for i := 0; i+2 <= l && off+2+i+2 <= end; i += 2 {
					g := uint16(raw[off+2+i])<<8 | uint16(raw[off+2+i+1])
					if g != 0x0a0a {
						curves = append(curves, fmt.Sprintf("%d", g))
					}
				}
			}
		case 0x000b: // ec_point_formats
			if elen >= 1 {
				l := int(raw[off])
				for i := 0; i < l && off+1+i < end; i++ {
					pointFmts = append(pointFmts, fmt.Sprintf("%d", raw[off+1+i]))
				}
			}
		}
		off += elen
	}
	h := sha256.New()
	h.Write([]byte(strings.Join(ciphers, "-") + "," +
		strings.Join(extIDs, "-") + "," +
		strings.Join(curves, "-") + "," +
		strings.Join(pointFmts, "-")))
	return hex.EncodeToString(h.Sum(nil))
}
