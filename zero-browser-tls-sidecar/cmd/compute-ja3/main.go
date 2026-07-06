// Tiny utility: connect to a host via the same uTLS preset the sidecar uses
// and print the JA3 hash + raw string of the ClientHello that was actually
// sent over the wire.
//
// Parses the raw ClientHello using the same cryptobyte helpers as
// crypto/tls's own ClientHello parser — this is what Go's TLS stack itself
// uses, so the result is byte-identical to what the server sees.

package main

import (
	"crypto/md5"
	"encoding/hex"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"strconv"
	"strings"

	utls "github.com/refraction-networking/utls"

	fp "github.com/moomok/zero-browser-tls-sidecar/fingerprint"

	"golang.org/x/crypto/cryptobyte"
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

	serverName := strings.Split(*host, ":")[0]
	tlsCfg := &utls.Config{ServerName: serverName, InsecureSkipVerify: true}
	uconn := utls.UClient(rawConn, tlsCfg, tpl.ClientHello)
	if err := uconn.Handshake(); err != nil {
		log.Fatalf("handshake: %v", err)
	}

	hello := uconn.HandshakeState.Hello
	if hello == nil {
		log.Fatal("no Hello in HandshakeState")
	}

	// Parse the marshaled ClientHello using cryptobyte (same parser as
	// crypto/tls itself).
	parsed, extOrder, err := parseClientHelloForJA3(hello.Raw)
	if err != nil {
		log.Fatalf("parse: %v", err)
	}

	ja3Raw := computeJA3Raw(parsed.vers, parsed.ciphers, extOrder, parsed.curves, parsed.points)
	sum := md5.Sum([]byte(ja3Raw))
	ja3Hash := hex.EncodeToString(sum[:])

	fmt.Println("JA3_RAW  =", ja3Raw)
	fmt.Println("JA3_HASH =", ja3Hash)

	fmt.Println("---")
	fmt.Println("TLSVersion     =", int(parsed.vers))
	fmt.Println("CipherSuites   =", joinUint16StripGREASE(parsed.ciphers))
	fmt.Println("Extensions     =", joinUint16StripGREASE(extOrder))
	fmt.Println("EllipticCurves =", joinUint16StripGREASE(parsed.curves))
	fmt.Println("ECPointFormats =", joinUint8(parsed.points))
}

type parsedHello struct {
	vers    uint16
	ciphers []uint16
	curves  []uint16
	points  []uint8
}

// parseClientHelloForJA3 parses the TLS handshake ClientHello message body
// (starts with type(1) + length(3)) and returns JA3-relevant fields plus
// extension IDs in transmission order.
func parseClientHelloForJA3(raw []byte) (*parsedHello, []uint16, error) {
	out := &parsedHello{}
	s := cryptobyte.String(raw)

	var hsType uint8
	var hsLen uint32
	if !s.ReadUint8(&hsType) || !s.ReadUint24(&hsLen) || hsType != 0x01 {
		return nil, nil, fmt.Errorf("not a ClientHello (hsType=%d)", hsType)
	}
	hsBody := cryptobyte.String(raw[4 : 4+int(hsLen)])
	body := hsBody

	var vers uint16
	if !body.ReadUint16(&vers) {
		return nil, nil, fmt.Errorf("read version")
	}
	out.vers = vers

	var random []byte
	if !body.ReadBytes(&random, 32) {
		return nil, nil, fmt.Errorf("read random")
	}

	var sid cryptobyte.String
	if !body.ReadUint8LengthPrefixed(&sid) {
		return nil, nil, fmt.Errorf("read session id")
	}

	var cs cryptobyte.String
	if !body.ReadUint16LengthPrefixed(&cs) {
		return nil, nil, fmt.Errorf("read cipher suites")
	}
	for !cs.Empty() {
		var c uint16
		if !cs.ReadUint16(&c) {
			return nil, nil, fmt.Errorf("read cipher")
		}
		out.ciphers = append(out.ciphers, c)
	}

	var comp cryptobyte.String
	if !body.ReadUint8LengthPrefixed(&comp) {
		return nil, nil, fmt.Errorf("read compression")
	}

	var exts cryptobyte.String
	if body.Empty() {
		return out, nil, nil
	}
	if !body.ReadUint16LengthPrefixed(&exts) {
		return nil, nil, fmt.Errorf("read extensions")
	}

	var extOrder []uint16
	for !exts.Empty() {
		var eid uint16
		var edata cryptobyte.String
		if !exts.ReadUint16(&eid) || !exts.ReadUint16LengthPrefixed(&edata) {
			return nil, nil, fmt.Errorf("read extension")
		}
		extOrder = append(extOrder, eid)
		switch eid {
		case 0x000a: // supported_groups / elliptic_curves
			var groups cryptobyte.String
			if !edata.ReadUint16LengthPrefixed(&groups) {
				return nil, nil, fmt.Errorf("read groups")
			}
			for !groups.Empty() {
				var g uint16
				if !groups.ReadUint16(&g) {
					return nil, nil, fmt.Errorf("read curve")
				}
				out.curves = append(out.curves, g)
			}
		case 0x000b: // ec_point_formats
			var pf cryptobyte.String
			if !edata.ReadUint8LengthPrefixed(&pf) {
				return nil, nil, fmt.Errorf("read point formats")
			}
			for !pf.Empty() {
				var p uint8
				if !pf.ReadUint8(&p) {
					return nil, nil, fmt.Errorf("read point fmt")
				}
				out.points = append(out.points, p)
			}
		}
	}

	return out, extOrder, nil
}

func computeJA3Raw(vers uint16, ciphers, exts, curves []uint16, points []uint8) string {
	return strings.Join([]string{
		strconv.Itoa(int(stripGREASE(vers))),
		joinUint16StripGREASE(ciphers),
		joinUint16StripGREASE(exts),
		joinUint16StripGREASE(curves),
		joinUint8(points),
	}, ",")
}

func joinUint16StripGREASE(vs []uint16) string {
	out := make([]string, 0, len(vs))
	for _, v := range vs {
		if isGREASE(v) {
			continue
		}
		out = append(out, strconv.Itoa(int(v)))
	}
	return strings.Join(out, "-")
}

func joinUint8(vs []uint8) string {
	out := make([]string, 0, len(vs))
	for _, v := range vs {
		out = append(out, strconv.Itoa(int(v)))
	}
	return strings.Join(out, "-")
}

func stripGREASE(v uint16) uint16 {
	if isGREASE(v) {
		return 0
	}
	return v
}

func isGREASE(v uint16) bool {
	return (v>>8) == (v&0xff) && v&0xf == 0xa
}
