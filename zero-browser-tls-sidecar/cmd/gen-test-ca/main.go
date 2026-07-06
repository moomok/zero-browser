// Tiny utility: generate a self-signed Root CA (cert + key PEM) for testing
// the sidecar locally without running the C# app.
//
// Usage: go run ./gen-test-ca --out C:\bot\zero-browser\test-ca
package main

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"flag"
	"fmt"
	"log"
	"math/big"
	"os"
	"path/filepath"
	"time"
)

func main() {
	out := flag.String("out", ".", "output directory")
	flag.Parse()

	if err := os.MkdirAll(*out, 0o700); err != nil {
		log.Fatal(err)
	}

	key, err := rsa.GenerateKey(rand.Reader, 2048) // 2048 to keep test fast; production uses 4096
	if err != nil {
		log.Fatal(err)
	}

	tpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "ZeroBrowser Test CA", Organization: []string{"ZeroBrowser"}},
		NotBefore:             time.Now().Add(-1 * time.Hour),
		NotAfter:              time.Now().Add(10 * 365 * 24 * time.Hour),
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageCRLSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		BasicConstraintsValid: true,
		IsCA:                  true,
		MaxPathLen:            1,
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &key.PublicKey, key)
	if err != nil {
		log.Fatal(err)
	}

	certPath := filepath.Join(*out, "test-ca.crt")
	keyPath := filepath.Join(*out, "test-ca.key.pem")

	certPem := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	if err := os.WriteFile(certPath, certPem, 0o644); err != nil {
		log.Fatal(err)
	}

	keyDer, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		log.Fatal(err)
	}
	keyPem := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDer})
	if err := os.WriteFile(keyPath, keyPem, 0o600); err != nil {
		log.Fatal(err)
	}
	fmt.Println("wrote", certPath, "and", keyPath)
}
