// Package actionful provides a client for invoking published mQuark Actionful endpoints.
package actionful

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

// jitterRatio is the fraction by which a poll wait is randomly spread, so a batch submitted together
// does not come back in lockstep.
const jitterRatio = 0.2

// Client invokes a single published Actionful endpoint.
type Client struct {
	endpointURL         string
	authHeader          string
	initialPollInterval time.Duration
	maxPollInterval     time.Duration
	http                *http.Client
}

// New creates a Client from the provided options.
// Returns an error if any required option is missing.
func New(opts Options) (*Client, error) {
	if err := opts.validate(); err != nil {
		return nil, err
	}
	raw := fmt.Sprintf("%s:%s", opts.AccessToken, opts.AccessSecret)
	auth := "Basic " + base64.StdEncoding.EncodeToString([]byte(raw))
	return &Client{
		endpointURL:         opts.EndpointURL,
		authHeader:          auth,
		initialPollInterval: opts.initialPollInterval(),
		maxPollInterval:     opts.maxPollInterval(),
		http:                &http.Client{},
	}, nil
}

// NewWithHTTPClient creates a Client using a custom *http.Client (e.g. for testing or transport customisation).
func NewWithHTTPClient(opts Options, httpClient *http.Client) (*Client, error) {
	c, err := New(opts)
	if err != nil {
		return nil, err
	}
	c.http = httpClient
	return c, nil
}

// ── Layer 1 · Raw async ────────────────────────────────────────────────────

// Submit posts the payload and returns a ticket immediately (always 202).
func (c *Client) Submit(ctx context.Context, payload string) (InvocationTicket, error) {
	req, err := c.newRequest(ctx, http.MethodPost, c.endpointURL, payload)
	if err != nil {
		return InvocationTicket{}, err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		return InvocationTicket{}, err
	}
	defer resp.Body.Close()

	if err := ensureSuccess(resp); err != nil {
		return InvocationTicket{}, err
	}
	return parseTicket(resp)
}

// SubmitJSON serialises input to JSON and calls Submit.
func SubmitJSON[TInput any](ctx context.Context, c *Client, input TInput) (InvocationTicket, error) {
	payload, err := json.Marshal(input)
	if err != nil {
		return InvocationTicket{}, fmt.Errorf("actionful: marshal input: %w", err)
	}
	return c.Submit(ctx, string(payload))
}

// GetJob polls once and returns the job's current state.
func (c *Client) GetJob(ctx context.Context, ticketOrURL any) (InvocationJob, error) {
	var pollURL string
	switch v := ticketOrURL.(type) {
	case InvocationTicket:
		pollURL = v.PollURL
	case string:
		pollURL = v
	default:
		return InvocationJob{}, fmt.Errorf("actionful: GetJob: unsupported argument type %T", ticketOrURL)
	}

	req, err := c.newRequest(ctx, http.MethodGet, pollURL, "")
	if err != nil {
		return InvocationJob{}, err
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return InvocationJob{}, err
	}
	defer resp.Body.Close()

	if err := ensureSuccess(resp); err != nil {
		return InvocationJob{}, err
	}

	jobID := extractJobID(pollURL)
	if resp.StatusCode == http.StatusOK {
		body, err := io.ReadAll(resp.Body)
		if err != nil {
			return InvocationJob{}, err
		}
		return InvocationJob{
			JobID:      jobID,
			Status:     StatusSucceeded,
			ResultJSON: string(body),
			IsTerminal: true,
		}, nil
	}
	return InvocationJob{
		JobID:      jobID,
		Status:     StatusRunning,
		IsTerminal: false,
	}, nil
}

// ── Layer 2 · Invoke and wait ──────────────────────────────────────────────

// InvokeRaw submits payload, polls until completion, and returns the raw result string.
func (c *Client) InvokeRaw(ctx context.Context, payload string) (string, error) {
	req, err := c.newRequest(ctx, http.MethodPost, c.endpointURL, payload)
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	if err := ensureSuccess(resp); err != nil {
		return "", err
	}

	if resp.StatusCode == http.StatusOK {
		body, err := io.ReadAll(resp.Body)
		if err != nil {
			return "", err
		}
		return string(body), nil
	}

	ticket, err := parseTicket(resp)
	if err != nil {
		return "", err
	}
	return c.pollUntilComplete(ctx, ticket.PollURL)
}

// Invoke serialises TInput, invokes the endpoint, and deserialises the result into TOutput.
func Invoke[TInput, TOutput any](ctx context.Context, c *Client, input TInput) (TOutput, error) {
	var zero TOutput
	payload, err := json.Marshal(input)
	if err != nil {
		return zero, fmt.Errorf("actionful: marshal input: %w", err)
	}
	resultJSON, err := c.InvokeRaw(ctx, string(payload))
	if err != nil {
		return zero, err
	}
	return deserialise[TOutput](resultJSON)
}

// ── Layer 3 · Batch ────────────────────────────────────────────────────────

// InvokeBatch processes all inputs and returns a slice of results when everything completes.
func InvokeBatch[TInput, TOutput any](ctx context.Context, c *Client, inputs []TInput, opts *BatchOptions) ([]InvocationResult[TOutput], error) {
	var out []InvocationResult[TOutput]
	for r := range StreamBatch[TInput, TOutput](ctx, c, inputs, opts) {
		out = append(out, r)
	}
	return out, ctx.Err()
}

// StreamBatch processes inputs concurrently and yields results via a channel as each completes.
func StreamBatch[TInput, TOutput any](ctx context.Context, c *Client, inputs []TInput, opts *BatchOptions) <-chan InvocationResult[TOutput] {
	o := opts.withDefaults()
	out := make(chan InvocationResult[TOutput], o.OutputBufferCapacity)
	sem := make(chan struct{}, o.MaxConcurrency)
	stopCh := make(chan struct{}, 1)

	go func() {
		defer close(out)
		var wg sync.WaitGroup

		for _, input := range inputs {
			select {
			case <-ctx.Done():
				goto wait
			case <-stopCh:
				goto wait
			case sem <- struct{}{}:
			}

			wg.Add(1)
			go func(in TInput) {
				defer wg.Done()
				defer func() { <-sem }()
				r := runWorker[TInput, TOutput](ctx, c, in)
				if !r.IsSuccess && o.StopOnFirstFailure {
					select {
					case stopCh <- struct{}{}:
					default:
					}
				}
				select {
				case out <- r:
				case <-ctx.Done():
				}
			}(input)
		}

	wait:
		wg.Wait()
	}()

	return out
}

// ── Layer 4 · Pipeline ─────────────────────────────────────────────────────

// Process accepts an input channel and returns a result channel, processing up to MaxConcurrency items concurrently.
// The caller closes the input channel to signal end of input; the returned channel is closed when all results are delivered.
func Process[TInput, TOutput any](ctx context.Context, c *Client, inputs <-chan TInput, opts *PipelineOptions) <-chan InvocationResult[TOutput] {
	o := opts.withDefaults()
	out := make(chan InvocationResult[TOutput], o.OutputBufferCapacity)
	sem := make(chan struct{}, o.MaxConcurrency)

	go func() {
		defer close(out)
		var wg sync.WaitGroup

		for {
			select {
			case <-ctx.Done():
				goto wait
			case input, ok := <-inputs:
				if !ok {
					goto wait
				}
				select {
				case sem <- struct{}{}:
				case <-ctx.Done():
					goto wait
				}
				wg.Add(1)
				go func(in TInput) {
					defer wg.Done()
					defer func() { <-sem }()
					r := runWorker[TInput, TOutput](ctx, c, in)
					select {
					case out <- r:
					case <-ctx.Done():
					}
				}(input)
			}
		}

	wait:
		wg.Wait()
	}()

	return out
}

// Pipeline is a long-running pipeline with explicit push/complete/iterate control.
type Pipeline[TInput, TOutput any] struct {
	input  chan TInput
	output <-chan InvocationResult[TOutput]
}

// NewPipeline creates a Pipeline backed by bounded input and output channels.
func NewPipeline[TInput, TOutput any](ctx context.Context, c *Client, opts *PipelineOptions) *Pipeline[TInput, TOutput] {
	o := opts.withDefaults()
	input := make(chan TInput, o.InputBufferCapacity)
	output := Process[TInput, TOutput](ctx, c, input, opts)
	return &Pipeline[TInput, TOutput]{input: input, output: output}
}

// Push sends an item to the pipeline. Blocks when the input buffer is full.
func (p *Pipeline[TInput, TOutput]) Push(ctx context.Context, item TInput) error {
	select {
	case p.input <- item:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Complete signals that no more items will be pushed. Must be called exactly once.
func (p *Pipeline[TInput, TOutput]) Complete() {
	close(p.input)
}

// Results returns the output channel. Range over it to consume results.
func (p *Pipeline[TInput, TOutput]) Results() <-chan InvocationResult[TOutput] {
	return p.output
}

// ── Internal ───────────────────────────────────────────────────────────────

func (c *Client) pollUntilComplete(ctx context.Context, pollURL string) (string, error) {
	backoff := c.initialPollInterval

	for {
		if err := ctx.Err(); err != nil {
			return "", err
		}

		req, err := c.newRequest(ctx, http.MethodGet, pollURL, "")
		if err != nil {
			return "", err
		}
		resp, err := c.http.Do(req)
		if err != nil {
			return "", err
		}

		if err := ensureSuccess(resp); err != nil {
			resp.Body.Close()
			return "", err
		}

		if resp.StatusCode == http.StatusOK {
			body, err := io.ReadAll(resp.Body)
			resp.Body.Close()
			return string(body), err
		}

		// 202 - still running. Retry-After is the server's instruction, not a suggestion: it knows what
		// this endpoint costs, and it outranks maxPollInterval, which bounds only our own backoff.
		wait := backoff
		if server := retryAfter(resp); server > wait {
			wait = server
		}
		resp.Body.Close()

		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(applyJitter(wait)):
		}

		if backoff *= 2; backoff > c.maxPollInterval {
			backoff = c.maxPollInterval
		}
	}
}

func runWorker[TInput, TOutput any](ctx context.Context, c *Client, input TInput) InvocationResult[TOutput] {
	payload, err := json.Marshal(input)
	if err != nil {
		return InvocationResult[TOutput]{Error: err.Error()}
	}

	ticket, err := c.Submit(ctx, string(payload))
	if err != nil {
		return InvocationResult[TOutput]{Ticket: ticket, Error: err.Error()}
	}

	resultJSON, err := c.pollUntilComplete(ctx, ticket.PollURL)
	if err != nil {
		return InvocationResult[TOutput]{Ticket: ticket, Error: err.Error()}
	}

	output, err := deserialise[TOutput](resultJSON)
	if err != nil {
		return InvocationResult[TOutput]{Ticket: ticket, Error: err.Error()}
	}
	return InvocationResult[TOutput]{Ticket: ticket, Output: &output, IsSuccess: true}
}

func (c *Client) newRequest(ctx context.Context, method, url, body string) (*http.Request, error) {
	var bodyReader io.Reader
	if body != "" {
		bodyReader = strings.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, url, bodyReader)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", c.authHeader)
	return req, nil
}

func ensureSuccess(resp *http.Response) error {
	if resp.StatusCode == http.StatusOK || resp.StatusCode == http.StatusAccepted {
		return nil
	}
	body, _ := io.ReadAll(resp.Body)
	aerr := &ActionfulError{StatusCode: resp.StatusCode, Body: strings.TrimSpace(string(body))}
	if resp.StatusCode == http.StatusTooManyRequests {
		if s := resp.Header.Get("Retry-After"); s != "" {
			aerr.RetryAfter, _ = strconv.Atoi(s)
		}
	}
	return aerr
}

func parseTicket(resp *http.Response) (InvocationTicket, error) {
	location := resp.Header.Get("Location")
	if location == "" {
		return InvocationTicket{}, fmt.Errorf("actionful: 202 response missing Location header")
	}
	// Resolve relative URLs (e.g. /jobs/abc123) against the request URL.
	if !strings.HasPrefix(location, "http") && resp.Request != nil && resp.Request.URL != nil {
		ref, err := url.Parse(location)
		if err != nil {
			return InvocationTicket{}, fmt.Errorf("actionful: parse Location header: %w", err)
		}
		location = resp.Request.URL.ResolveReference(ref).String()
	}
	return InvocationTicket{
		JobID:       extractJobID(location),
		PollURL:     location,
		SubmittedAt: time.Now().UTC(),
	}, nil
}

func extractJobID(url string) string {
	url = strings.TrimRight(url, "/")
	idx := strings.LastIndex(url, "/")
	if idx < 0 {
		return url
	}
	return url[idx+1:]
}

func retryAfter(resp *http.Response) time.Duration {
	if s := resp.Header.Get("Retry-After"); s != "" {
		if secs, err := strconv.Atoi(s); err == nil {
			return time.Duration(secs) * time.Second
		}
	}
	return 0
}

func applyJitter(d time.Duration) time.Duration {
	return time.Duration(float64(d) * (1 + (rand.Float64()-0.5)*jitterRatio))
}

func deserialise[T any](jsonStr string) (T, error) {
	var out T
	// If T is string, return the raw JSON without parsing.
	if _, ok := any(&out).(*string); ok {
		*any(&out).(*string) = jsonStr
		return out, nil
	}
	if err := json.Unmarshal([]byte(jsonStr), &out); err != nil {
		return out, fmt.Errorf("actionful: deserialise: %w", err)
	}
	return out, nil
}
