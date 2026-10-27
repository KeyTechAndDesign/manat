package manat

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Doer sends HTTP requests; *http.Client is one. Pass your own in a config's HTTPClient.
type Doer interface {
	Do(req *http.Request) (*http.Response, error)
}

const defaultTimeout = 30 * time.Second

// Version is this module's release.
const Version = "0.2.2"

// userAgent is sent with every request so providers can tell the SDK apart from generic clients.
const userAgent = "keytd-manat/" + Version + " (go; +https://github.com/KeyTechAndDesign/manat)"

var defaultClient = &http.Client{
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
}

// transport wraps a Doer for one adapter: encodes bodies, applies the timeout and turns failures into *Error.
type transport struct {
	provider string
	doer     Doer
	timeout  time.Duration
}

func newTransport(provider string, doer Doer, timeout time.Duration) transport {
	if doer == nil {
		doer = defaultClient
	}
	if timeout <= 0 {
		timeout = defaultTimeout
	}
	return transport{provider: provider, doer: doer, timeout: timeout}
}

// send writes json (unless nil) or, when form is not nil, an urlencoded body.
func (t transport) send(ctx context.Context, method, url string, headers []pair, json any, form []pair) (int, string, error) {
	var body io.Reader
	contentType := ""
	if form != nil {
		body = strings.NewReader(formEncode(form))
		contentType = "application/x-www-form-urlencoded"
	} else if json != nil {
		body = strings.NewReader(stringify(json))
		contentType = "application/json"
	}
	ctx, cancel := context.WithTimeout(ctx, t.timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, method, url, body)
	if err != nil {
		return 0, "", &Error{Code: InvalidRequest, Message: fmt.Sprintf("%s: %s %s %v", t.provider, method, url, err), Provider: t.provider, Err: err}
	}
	req.Header.Set("user-agent", userAgent)
	for _, h := range headers {
		req.Header.Set(h.k, h.v)
	}
	if contentType != "" && req.Header.Get("content-type") == "" {
		req.Header.Set("content-type", contentType)
	}
	res, err := t.doer.Do(req)
	if err != nil {
		var e *Error
		if errors.As(err, &e) {
			return 0, "", e
		}
		reason := err.Error()
		if errors.Is(err, context.DeadlineExceeded) {
			reason = fmt.Sprintf("timed out after %s", t.timeout)
		}
		return 0, "", &Error{Code: NetworkError, Message: fmt.Sprintf("%s: %s %s %s", t.provider, method, url, reason), Provider: t.provider, Err: err}
	}
	defer res.Body.Close()
	var b bytes.Buffer
	if _, err := io.Copy(&b, res.Body); err != nil {
		return 0, "", &Error{Code: NetworkError, Message: fmt.Sprintf("%s: %s %s %v", t.provider, method, url, err), Provider: t.provider, Err: err}
	}
	return res.StatusCode, b.String(), nil
}

// json is send() and parses the reply as JSON (nil for an empty body).
func (t transport) json(ctx context.Context, method, url string, headers []pair, json any, form []pair) (int, any, error) {
	status, body, err := t.send(ctx, method, url, append([]pair{{"accept", "application/json"}}, headers...), json, form)
	if err != nil {
		return 0, nil, err
	}
	if body == "" {
		return status, nil, nil
	}
	data, perr := parseJSON(body)
	if perr != nil {
		excerpt := body
		if len(excerpt) > 200 {
			excerpt = excerpt[:200]
		}
		return 0, nil, &Error{Code: NetworkError, Message: fmt.Sprintf("%s: expected JSON, got HTTP %d %q", t.provider, status, excerpt),
			Provider: t.provider, Raw: body, Err: perr}
	}
	return status, data, nil
}
