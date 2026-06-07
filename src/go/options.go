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

	// PollInterval is the minimum wait between poll attempts (default 2s).
	// The server's Retry-After header wins when it suggests waiting longer.
	PollInterval time.Duration
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

func (o *Options) pollInterval() time.Duration {
	if o.PollInterval > 0 {
		return o.PollInterval
	}
	return 2 * time.Second
}
