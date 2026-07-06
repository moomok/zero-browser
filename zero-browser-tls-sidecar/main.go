package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/moomok/zero-browser-tls-sidecar/fingerprint"
	"github.com/moomok/zero-browser-tls-sidecar/health"
	"github.com/moomok/zero-browser-tls-sidecar/mitm"
)

type cliArgs struct {
	port          int
	profileID     string
	fpMode        string
	seed          string
	caCert        string
	caKey         string
	upstreamProxy string
	verbose       bool
}

func parseArgs() (*cliArgs, error) {
	a := &cliArgs{}
	flag.IntVar(&a.port, "port", 0, "port to listen on (0 = pick a free port)")
	flag.StringVar(&a.profileID, "profile-id", "", "profile UUID (for logging)")
	flag.StringVar(&a.fpMode, "fingerprint-mode", "chrome", "chrome|firefox|safari|edge|randomized")
	flag.StringVar(&a.seed, "seed", "", "profile fingerprint seed (deterministic template selector)")
	flag.StringVar(&a.caCert, "ca-cert", "", "path to PEM-encoded Root CA cert")
	flag.StringVar(&a.caKey, "ca-key", "", "path to PEM-encoded Root CA private key")
	flag.StringVar(&a.upstreamProxy, "upstream-proxy", "", "optional upstream proxy URL (e.g. socks5://user:pass@host:1080 or http://...)")
	flag.BoolVar(&a.verbose, "verbose", false, "verbose logging")
	flag.Parse()

	if a.fpMode == "" {
		return nil, errors.New("--fingerprint-mode is required")
	}
	if a.seed == "" {
		return nil, errors.New("--seed is required")
	}
	if a.caCert == "" || a.caKey == "" {
		return nil, errors.New("--ca-cert and --ca-key are required")
	}
	return a, nil
}

func findFreePort() (int, error) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port, nil
}

func structuredLog(fields map[string]any) {
	b, _ := json.Marshal(fields)
	fmt.Fprintln(os.Stderr, string(b))
}

func main() {
	args, err := parseArgs()
	if err != nil {
		log.Fatalf("arg error: %v", err)
	}

	if args.port == 0 {
		p, err := findFreePort()
		if err != nil {
			log.Fatalf("find free port: %v", err)
		}
		args.port = p
	}

	template, err := fingerprint.Select(args.seed, args.fpMode)
	if err != nil {
		log.Fatalf("fingerprint select: %v", err)
	}
	structuredLog(map[string]any{
		"event":   "fingerprint_selected",
		"profile": args.profileID,
		"mode":    args.fpMode,
		"seed":    args.seed,
		"id":      template.ID,
	})

	ca, err := mitm.LoadCA(args.caCert, args.caKey)
	if err != nil {
		log.Fatalf("load CA: %v", err)
	}

	var upstream *url.URL
	if args.upstreamProxy != "" {
		upstream, err = url.Parse(args.upstreamProxy)
		if err != nil {
			log.Fatalf("parse upstream proxy: %v", err)
		}
	}

	listener, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", args.port))
	if err != nil {
		log.Fatalf("listen: %v", err)
	}

	var leafCache sync.Map // reserved for future per-host cert cache
	_ = leafCache // suppress unused warning

	handler := &mitm.Handler{
		CA:            ca,
		Template:      template,
		UpstreamProxy: upstream,
		Verbose:       args.verbose,
		OnConnection: func(host string) {
			structuredLog(map[string]any{
				"event":   "tls_handshake",
				"profile": args.profileID,
				"host":    host,
				"id":      template.ID,
			})
		},
	}

	healthSrv := &http.Server{
		Addr:    fmt.Sprintf("127.0.0.1:%d", args.port+1),
		Handler: health.New(args.profileID, template.ID),
	}
	go func() {
		if err := healthSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			structuredLog(map[string]any{"event": "health_error", "error": err.Error()})
		}
	}()

	server := &http.Server{
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.Method == http.MethodConnect {
				handler.HandleConnect(w, r)
				return
			}
			// Reject non-CONNECT with a clear error.
			w.Header().Set("Content-Type", "text/plain")
			w.WriteHeader(http.StatusBadRequest)
			_, _ = w.Write([]byte("zero-browser-tls-sidecar: only CONNECT is supported\r\n"))
		}),
		TLSConfig: &tls.Config{GetCertificate: ca.GetCertificate}, // unused for CONNECT, kept defensive
	}

	go func() {
		structuredLog(map[string]any{
			"event":     "ready",
			"profile":   args.profileID,
			"port":      args.port,
			"health":    args.port + 1,
			"id":        template.ID,
		})
		if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
			structuredLog(map[string]any{"event": "serve_error", "error": err.Error()})
		}
	}()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	<-sig

	structuredLog(map[string]any{"event": "shutting_down", "profile": args.profileID})
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = server.Shutdown(shutdownCtx)
	_ = healthSrv.Shutdown(shutdownCtx)
}
