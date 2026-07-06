package health

import (
	"encoding/json"
	"net/http"
	"time"
)

// New returns a minimal HTTP handler that responds 200 OK with JSON metadata
// describing which sidecar instance is alive and which uTLS template it has
// selected. Used by the C# launcher to block until the sidecar is ready
// before pointing Chromium at --proxy-server=http://127.0.0.1:<port>.
func New(profileID, templateID string) http.Handler {
	startTime := time.Now()
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/health" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"status":     "ready",
			"profile":    profileID,
			"template":   templateID,
			"started_at": startTime.UTC().Format(time.RFC3339),
		})
	})
}
