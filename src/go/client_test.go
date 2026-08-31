package actionful_test

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	actionful "github.com/m-quark/ActionfulClient/src/go/v2"
)

// ── Helpers ────────────────────────────────────────────────────────────────

type order struct {
	ID    int    `json:"id"`
	Value string `json:"value"`
}

type riskScore struct {
	Score float64 `json:"score"`
}

// fakeServer returns an httptest.Server that:
//   - POST /endpoint → 202 with Location: /jobs/abc123 (if async=true) or 200 with body (if async=false)
//   - GET  /jobs/{id} → 200 with body after pollCount calls have been made
func fakeServer(t *testing.T, resultBody string, async bool, pollCount int) *httptest.Server {
	t.Helper()
	polls := atomic.Int32{}

	mux := http.NewServeMux()
	mux.HandleFunc("/endpoint", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		if async {
			w.Header().Set("Location", "/jobs/abc123")
			w.WriteHeader(http.StatusAccepted)
			return
		}
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, resultBody)
	})

	mux.HandleFunc("/jobs/abc123", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		n := polls.Add(1)
		if int(n) < pollCount {
			w.Header().Set("Retry-After", "0")
			w.WriteHeader(http.StatusAccepted)
			return
		}
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, resultBody)
	})

	return httptest.NewServer(mux)
}

func newTestClient(t *testing.T, srv *httptest.Server) *actionful.Client {
	t.Helper()
	c, err := actionful.NewWithHTTPClient(actionful.Options{
		EndpointURL:         srv.URL + "/endpoint",
		AccessToken:         "tok",
		AccessSecret:        "sec",
		InitialPollInterval: 1 * time.Millisecond,
		MaxPollInterval:     10 * time.Millisecond,
	}, srv.Client())
	if err != nil {
		t.Fatalf("NewWithHTTPClient: %v", err)
	}
	return c
}

// ── Layer 1 · Submit / GetJob ──────────────────────────────────────────────

func TestSubmit_Returns202Ticket(t *testing.T) {
	srv := fakeServer(t, `{"score":0.9}`, true, 1)
	defer srv.Close()
	c := newTestClient(t, srv)

	ticket, err := c.Submit(context.Background(), `{"id":1}`)
	if err != nil {
		t.Fatalf("Submit: %v", err)
	}
	if ticket.JobID == "" {
		t.Error("expected non-empty JobID")
	}
	if ticket.PollURL == "" {
		t.Error("expected non-empty PollURL")
	}
}

func TestGetJob_ReturnsSucceededAfterPoll(t *testing.T) {
	srv := fakeServer(t, `{"score":0.9}`, true, 2)
	defer srv.Close()
	c := newTestClient(t, srv)

	ticket, err := c.Submit(context.Background(), `{"id":1}`)
	if err != nil {
		t.Fatalf("Submit: %v", err)
	}

	// First poll → still running
	job, err := c.GetJob(context.Background(), ticket)
	if err != nil {
		t.Fatalf("GetJob (1): %v", err)
	}
	if job.Status != actionful.StatusRunning {
		t.Errorf("expected running, got %s", job.Status)
	}

	// Second poll → succeeded
	job, err = c.GetJob(context.Background(), ticket)
	if err != nil {
		t.Fatalf("GetJob (2): %v", err)
	}
	if job.Status != actionful.StatusSucceeded {
		t.Errorf("expected succeeded, got %s", job.Status)
	}
	if job.ResultJSON == "" {
		t.Error("expected non-empty ResultJSON")
	}
}

// ── Layer 2 · InvokeRaw / Invoke ──────────────────────────────────────────

func TestInvokeRaw_FastPath(t *testing.T) {
	srv := fakeServer(t, `{"score":0.9}`, false, 0)
	defer srv.Close()
	c := newTestClient(t, srv)

	result, err := c.InvokeRaw(context.Background(), `{"id":1}`)
	if err != nil {
		t.Fatalf("InvokeRaw: %v", err)
	}
	if result != `{"score":0.9}` {
		t.Errorf("unexpected result: %s", result)
	}
}

func TestInvokeRaw_AsyncPath(t *testing.T) {
	srv := fakeServer(t, `{"score":0.7}`, true, 3)
	defer srv.Close()
	c := newTestClient(t, srv)

	result, err := c.InvokeRaw(context.Background(), `{"id":2}`)
	if err != nil {
		t.Fatalf("InvokeRaw: %v", err)
	}
	if result == "" {
		t.Error("expected non-empty result")
	}
}

func TestInvoke_TypedDeserialization(t *testing.T) {
	body, _ := json.Marshal(riskScore{Score: 0.85})
	srv := fakeServer(t, string(body), false, 0)
	defer srv.Close()
	c := newTestClient(t, srv)

	score, err := actionful.Invoke[order, riskScore](context.Background(), c, order{ID: 1, Value: "test"})
	if err != nil {
		t.Fatalf("Invoke: %v", err)
	}
	if score.Score != 0.85 {
		t.Errorf("expected 0.85, got %f", score.Score)
	}
}

func TestInvoke_StringOutput(t *testing.T) {
	srv := fakeServer(t, "plain text result", false, 0)
	defer srv.Close()
	c := newTestClient(t, srv)

	result, err := actionful.Invoke[order, string](context.Background(), c, order{ID: 1})
	if err != nil {
		t.Fatalf("Invoke: %v", err)
	}
	if result != "plain text result" {
		t.Errorf("unexpected result: %s", result)
	}
}

func TestInvoke_CancellationDuringPoll(t *testing.T) {
	srv := fakeServer(t, `{"score":0.5}`, true, 1000)
	defer srv.Close()
	c := newTestClient(t, srv)

	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()

	_, err := c.InvokeRaw(ctx, `{"id":1}`)
	if err == nil {
		t.Fatal("expected cancellation error, got nil")
	}
}

// ── Layer 3 · InvokeBatch / StreamBatch ───────────────────────────────────

func TestInvokeBatch_AllSucceed(t *testing.T) {
	body, _ := json.Marshal(riskScore{Score: 0.5})
	srv := fakeServer(t, string(body), true, 1)
	defer srv.Close()
	c := newTestClient(t, srv)

	inputs := []order{{ID: 1}, {ID: 2}, {ID: 3}}
	results, err := actionful.InvokeBatch[order, riskScore](context.Background(), c, inputs, nil)
	if err != nil {
		t.Fatalf("InvokeBatch: %v", err)
	}
	if len(results) != 3 {
		t.Errorf("expected 3 results, got %d", len(results))
	}
	for _, r := range results {
		if !r.IsSuccess {
			t.Errorf("expected success, got error: %s", r.Error)
		}
	}
}

func TestInvokeBatch_PartialFailure(t *testing.T) {
	callCount := atomic.Int32{}
	mux := http.NewServeMux()
	mux.HandleFunc("/endpoint", func(w http.ResponseWriter, r *http.Request) {
		n := callCount.Add(1)
		if n%2 == 0 {
			http.Error(w, "forced error", http.StatusInternalServerError)
			return
		}
		// Async path: 202 + Location.
		w.Header().Set("Location", "/jobs/ok")
		w.WriteHeader(http.StatusAccepted)
	})
	mux.HandleFunc("/jobs/ok", func(w http.ResponseWriter, r *http.Request) {
		body, _ := json.Marshal(riskScore{Score: 0.5})
		w.WriteHeader(http.StatusOK)
		w.Write(body)
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()
	c := newTestClient(t, srv)

	inputs := []order{{ID: 1}, {ID: 2}, {ID: 3}, {ID: 4}}
	results, _ := actionful.InvokeBatch[order, riskScore](context.Background(), c, inputs, nil)
	if len(results) != 4 {
		t.Errorf("expected 4 results, got %d", len(results))
	}

	successes, failures := 0, 0
	for _, r := range results {
		if r.IsSuccess {
			successes++
		} else {
			failures++
		}
	}
	if successes == 0 || failures == 0 {
		t.Errorf("expected mix of success/failure, got %d/%d", successes, failures)
	}
}

func TestStreamBatch_StopOnFirstFailure(t *testing.T) {
	callCount := atomic.Int32{}
	mux := http.NewServeMux()
	mux.HandleFunc("/endpoint", func(w http.ResponseWriter, r *http.Request) {
		n := callCount.Add(1)
		if n == 1 {
			http.Error(w, "first failure", http.StatusInternalServerError)
			return
		}
		body, _ := json.Marshal(riskScore{Score: 0.5})
		w.WriteHeader(http.StatusOK)
		w.Write(body)
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()
	c := newTestClient(t, srv)

	// 100 items but StopOnFirstFailure — should emit far fewer than 100 results.
	inputs := make([]order, 100)
	for i := range inputs {
		inputs[i] = order{ID: i}
	}

	opts := &actionful.BatchOptions{MaxConcurrency: 1, StopOnFirstFailure: true}
	var count int
	for range actionful.StreamBatch[order, riskScore](context.Background(), c, inputs, opts) {
		count++
	}
	if count >= len(inputs) {
		t.Errorf("StopOnFirstFailure did not stop early: got %d results for %d inputs", count, len(inputs))
	}
}

// ── Layer 4 · Pipeline ─────────────────────────────────────────────────────

func TestProcess_StreamsResults(t *testing.T) {
	body, _ := json.Marshal(riskScore{Score: 0.3})
	srv := fakeServer(t, string(body), true, 1)
	defer srv.Close()
	c := newTestClient(t, srv)

	input := make(chan order, 3)
	input <- order{ID: 1}
	input <- order{ID: 2}
	input <- order{ID: 3}
	close(input)

	var results []actionful.InvocationResult[riskScore]
	for r := range actionful.Process[order, riskScore](context.Background(), c, input, nil) {
		results = append(results, r)
	}
	if len(results) != 3 {
		t.Errorf("expected 3 results, got %d", len(results))
	}
}

func TestNewPipeline_PushAndIterate(t *testing.T) {
	body, _ := json.Marshal(riskScore{Score: 0.6})
	srv := fakeServer(t, string(body), true, 1)
	defer srv.Close()
	c := newTestClient(t, srv)

	ctx := context.Background()
	pipeline := actionful.NewPipeline[order, riskScore](ctx, c, nil)

	go func() {
		for i := 0; i < 5; i++ {
			pipeline.Push(ctx, order{ID: i})
		}
		pipeline.Complete()
	}()

	count := 0
	for r := range pipeline.Results() {
		if !r.IsSuccess {
			t.Errorf("unexpected error: %s", r.Error)
		}
		count++
	}
	if count != 5 {
		t.Errorf("expected 5 results, got %d", count)
	}
}

// ── Error handling ─────────────────────────────────────────────────────────

func TestSubmit_HttpError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "bad request", http.StatusBadRequest)
	}))
	defer srv.Close()
	c := newTestClient(t, srv)

	_, err := c.Submit(context.Background(), `{}`)
	if err == nil {
		t.Fatal("expected error, got nil")
	}
	aerr, ok := err.(*actionful.ActionfulError)
	if !ok {
		t.Fatalf("expected ActionfulError, got %T", err)
	}
	if aerr.StatusCode != http.StatusBadRequest {
		t.Errorf("expected 400, got %d", aerr.StatusCode)
	}
}

func TestSubmit_RateLimitError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Retry-After", "30")
		http.Error(w, "rate limited", http.StatusTooManyRequests)
	}))
	defer srv.Close()
	c := newTestClient(t, srv)

	_, err := c.Submit(context.Background(), `{}`)
	aerr, ok := err.(*actionful.ActionfulError)
	if !ok {
		t.Fatalf("expected ActionfulError, got %T", err)
	}
	if aerr.RetryAfter != 30 {
		t.Errorf("expected RetryAfter=30, got %d", aerr.RetryAfter)
	}
}

// ── Options validation ─────────────────────────────────────────────────────

func TestNew_MissingEndpointURL(t *testing.T) {
	_, err := actionful.New(actionful.Options{AccessToken: "t", AccessSecret: "s"})
	if err == nil {
		t.Fatal("expected error for missing EndpointURL")
	}
}

func TestNew_MissingAccessToken(t *testing.T) {
	_, err := actionful.New(actionful.Options{EndpointURL: "https://example.com", AccessSecret: "s"})
	if err == nil {
		t.Fatal("expected error for missing AccessToken")
	}
}

// The server holds no connection and reads no wait preference; asking for one advertises a contract
// that does not exist. See docs/design/actionful-client-sdk.md.
func TestSubmit_NegotiatesNoServerSideWait(t *testing.T) {
	var got http.Header
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Clone()
		w.Header().Set("Location", "/endpoint/job-1")
		w.WriteHeader(http.StatusAccepted)
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	if _, err := c.Submit(context.Background(), `{}`); err != nil {
		t.Fatalf("Submit: %v", err)
	}

	for _, h := range []string{"Mq-Timeout-Seconds", "Prefer"} {
		if v := got.Get(h); v != "" {
			t.Errorf("client must not send %s, got %q", h, v)
		}
	}
}
