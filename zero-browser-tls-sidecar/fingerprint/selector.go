package fingerprint

import (
	"crypto/sha256"
	"encoding/binary"
	"strings"

	utls "github.com/refraction-networking/utls"
)

// Template is a single uTLS preset bundled with metadata.
type Template struct {
	ID          string
	ClientHello utls.ClientHelloID
}

// Select deterministically picks a Template given a seed and a mode string.
// mode="chrome"|"firefox"|"safari"|"edge" maps to a fixed preset.
// mode="randomized" picks one of all presets using the seed.
// Any other mode falls back to Chrome.
//
// The selection algorithm MUST be identical to the C# TlsFingerprintPool
// selector when both code paths are active for the same profile (e.g. the
// C# Test button + this sidecar must agree on what template will be used).
// Hashing is SHA-256(seed) mod |pool|.
func Select(seed, mode string) (Template, error) {
	switch strings.ToLower(mode) {
	case "chrome":
		return Template{ID: "Chrome_106_Shuffle", ClientHello: utls.HelloChrome_106_Shuffle}, nil
	case "firefox":
		return Template{ID: "Firefox_105", ClientHello: utls.HelloFirefox_105}, nil
	case "safari":
		return Template{ID: "Safari_16_0", ClientHello: utls.HelloSafari_16_0}, nil
	case "edge":
		return Template{ID: "Edge_106", ClientHello: utls.HelloEdge_106}, nil
	case "randomized":
		all := []Template{
			{ID: "Chrome_106_Shuffle", ClientHello: utls.HelloChrome_106_Shuffle},
			{ID: "Chrome_100", ClientHello: utls.HelloChrome_100},
			{ID: "Chrome_102", ClientHello: utls.HelloChrome_102},
			{ID: "Firefox_105", ClientHello: utls.HelloFirefox_105},
			{ID: "Firefox_102", ClientHello: utls.HelloFirefox_102},
			{ID: "Firefox_99", ClientHello: utls.HelloFirefox_99},
			{ID: "Safari_16_0", ClientHello: utls.HelloSafari_16_0},
			{ID: "Edge_106", ClientHello: utls.HelloEdge_106},
			{ID: "Edge_85", ClientHello: utls.HelloEdge_85},
			{ID: "IOS_14", ClientHello: utls.HelloIOS_14},
		}
		idx := hashSeedMod(seed, len(all))
		return all[idx], nil
	default:
		// Unknown mode: fall back to Chrome (matches C# fallback behavior).
		return Template{ID: "Chrome_106_Shuffle", ClientHello: utls.HelloChrome_106_Shuffle}, nil
	}
}

// hashSeedMod returns a stable non-negative index in [0,n) for any seed string.
// Uses SHA-256(seed) -> first 8 bytes interpreted as uint64 -> mod n.
func hashSeedMod(seed string, n int) int {
	if n <= 0 {
		return 0
	}
	h := sha256.Sum256([]byte(seed))
	v := binary.BigEndian.Uint64(h[:8])
	return int(v % uint64(n))
}
