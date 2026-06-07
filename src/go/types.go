package actionful

import (
	"fmt"
	"time"
)

// InvocationStatus represents the state of a submitted job.
type InvocationStatus string

const (
	StatusPending   InvocationStatus = "pending"
	StatusRunning   InvocationStatus = "running"
	StatusSucceeded InvocationStatus = "succeeded"
	StatusFailed    InvocationStatus = "failed"
	StatusCancelled InvocationStatus = "cancelled"
)

func (s InvocationStatus) IsTerminal() bool {
	return s == StatusSucceeded || s == StatusFailed || s == StatusCancelled
}

// InvocationTicket is returned immediately on submit.
type InvocationTicket struct {
	JobID       string
	PollURL     string
	SubmittedAt time.Time
}

// InvocationJob is the current state of a submitted job.
type InvocationJob struct {
	JobID      string
	Status     InvocationStatus
	ResultJSON string // non-empty when Status == StatusSucceeded
	Error      string // non-empty when Status == StatusFailed
	IsTerminal bool
}

// InvocationResult wraps the outcome of one batch or pipeline item.
type InvocationResult[TOutput any] struct {
	Ticket    InvocationTicket
	Output    *TOutput
	Error     string
	IsSuccess bool
}

// ActionfulError is returned for non-2xx HTTP responses.
type ActionfulError struct {
	StatusCode int
	Body       string
	RetryAfter int // seconds; populated on 429
}

func (e *ActionfulError) Error() string {
	return fmt.Sprintf("actionful: HTTP %d: %s", e.StatusCode, e.Body)
}

// BatchOptions controls Layer 3 batch behaviour.
type BatchOptions struct {
	MaxConcurrency      int  // default 10
	StopOnFirstFailure  bool // default false
	OutputBufferCapacity int  // default 1000
}

func (o *BatchOptions) withDefaults() BatchOptions {
	out := BatchOptions{MaxConcurrency: 10, OutputBufferCapacity: 1000}
	if o != nil {
		if o.MaxConcurrency > 0 {
			out.MaxConcurrency = o.MaxConcurrency
		}
		if o.OutputBufferCapacity > 0 {
			out.OutputBufferCapacity = o.OutputBufferCapacity
		}
		out.StopOnFirstFailure = o.StopOnFirstFailure
	}
	return out
}

// PipelineOptions controls Layer 4 pipeline behaviour.
type PipelineOptions struct {
	MaxConcurrency       int  // default 10
	InputBufferCapacity  int  // default 1000
	OutputBufferCapacity int  // default 1000
}

func (o *PipelineOptions) withDefaults() PipelineOptions {
	out := PipelineOptions{MaxConcurrency: 10, InputBufferCapacity: 1000, OutputBufferCapacity: 1000}
	if o != nil {
		if o.MaxConcurrency > 0 {
			out.MaxConcurrency = o.MaxConcurrency
		}
		if o.InputBufferCapacity > 0 {
			out.InputBufferCapacity = o.InputBufferCapacity
		}
		if o.OutputBufferCapacity > 0 {
			out.OutputBufferCapacity = o.OutputBufferCapacity
		}
	}
	return out
}
