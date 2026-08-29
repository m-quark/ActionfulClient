package actionful

import (
	"errors"
	"time"
)

// Options holds the configuration for an ActionfulClient.
type Options struct {
	// EndpointURL is the published Actionful endpoint URL (required).
	EndpointURL string

	// AccessToken and AccessSecret are shown in the Actionful Web UI (required).
	AccessToken  string
	AccessSecret string

	// InitialPollInterval is the wait before the first poll of a job that is still running
	// (default 250ms). Subsequent waits double up to MaxPollInterval, with jitter.
	InitialPollInterval time.Duration

	// MaxPollInterval bounds the wait between polls (default 5s). A Retry-After from the server
	// outranks it.
	MaxPollInterval time.Duration
}

func (o *Options) validate() error {
	if o.EndpointURL == "" {
		return errors.New("actionful: EndpointURL is required")
	}
	if o.AccessToken == "" {
		return errors.New("actionful: AccessToken is required")
	}
	if o.AccessSecret == "" {
		return errors.New("actionful: AccessSecret is required")
	}
	return nil
}

func (o *Options) initialPollInterval() time.Duration {
	if o.InitialPollInterval > 0 {
		return o.InitialPollInterval
	}
	return 250 * time.Millisecond
}

func (o *Options) maxPollInterval() time.Duration {
	if o.MaxPollInterval > 0 {
		return o.MaxPollInterval
	}
	return 5 * time.Second
}
