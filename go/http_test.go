package manat

import (
	"context"
	"io"
	"net/http"
	"strings"
	"testing"
)

type captureDoer struct{ header http.Header }

func (c *captureDoer) Do(req *http.Request) (*http.Response, error) {
	c.header = req.Header.Clone()
	return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader("{}")), Header: http.Header{}}, nil
}

func TestRequestsIdentifyTheSDK(t *testing.T) {
	doer := &captureDoer{}
	p, err := NewPayriff(PayriffConfig{SecretKey: "k", HTTPClient: doer})
	if err != nil {
		t.Fatal(err)
	}
	_, _ = p.GetStatus(context.Background(), "A-1")
	want := "keytd-manat/" + Version + " (go; +https://github.com/KeyTechAndDesign/manat)"
	if got := doer.header.Get("User-Agent"); got != want {
		t.Fatalf("user-agent %q, want %q", got, want)
	}
}
