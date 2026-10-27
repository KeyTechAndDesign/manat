package manat

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"
)

const vectorsDir = "../spec/vectors"

// TestVectors runs every vector in spec/vectors, the same files the other languages run.
func TestVectors(t *testing.T) {
	files, err := filepath.Glob(filepath.Join(vectorsDir, "*", "*.json"))
	if err != nil || len(files) < 100 {
		t.Fatalf("vectors not found (%d files, %v)", len(files), err)
	}
	sort.Strings(files)
	for _, file := range files {
		file := file
		name := filepath.Base(filepath.Dir(file)) + "/" + filepath.Base(file)
		t.Run(name, func(t *testing.T) {
			content, err := os.ReadFile(file)
			if err != nil {
				t.Fatal(err)
			}
			parsed, err := parseJSON(string(content))
			if err != nil {
				t.Fatal(err)
			}
			runVector(t, asMap(parsed))
		})
	}
}

func runVector(t *testing.T, vector map[string]any) {
	provider, _ := str(vector["provider"])
	operation, _ := str(vector["operation"])
	config := asMap(vector["config"])

	switch operation {
	case "mapStatus":
		for _, c := range asList(vector["cases"]) {
			input := asMap(get(c, "input"))
			got, err := mapStatus(provider, input)
			if err != nil || string(got) != get(c, "expected", "status") {
				t.Errorf("%s: got %q (%v), want %v", stringify(input), got, err, get(c, "expected", "status"))
			}
		}
		return
	case "macSource":
		for _, c := range asList(vector["cases"]) {
			fields := map[string]string{}
			for k, v := range asMap(get(c, "input", "fields")) {
				fields[k], _ = str(v)
			}
			var order []string
			for _, f := range asList(get(c, "input", "order")) {
				s, _ := str(f)
				order = append(order, s)
			}
			if got := AzericardMacSource(fields, order); got != get(c, "expected", "source") {
				t.Errorf("macSource = %q, want %v", got, get(c, "expected", "source"))
			}
		}
		return
	case "sign":
		key, _ := str(config["privateKey"])
		for _, c := range asList(vector["cases"]) {
			jsonText, _ := str(get(c, "input", "json"))
			data, signature := EpointEncode(key, jsonText)
			if data != get(c, "expected", "data") || signature != get(c, "expected", "signature") {
				t.Errorf("sign: got %s %s", data, signature)
			}
		}
		return
	}

	transport := &scripted{t: t, steps: asList(vector["http"])}
	p, err := buildProvider(provider, config, transport)
	if err != nil {
		t.Fatalf("provider: %v", err)
	}
	expected := asMap(vector["expected"])
	if expected == nil {
		expected = map[string]any{}
	}
	result, err := invoke(New(p), operation, asMap(vector["input"]))
	if want, ok := expected["error"]; ok {
		var e *Error
		if !errors.As(err, &e) {
			t.Fatalf("expected error %v, got result %v / %v", want, result, err)
		}
		if string(e.Code) != want {
			t.Fatalf("expected %v, got %s: %s", want, e.Code, e.Message)
		}
		if code, ok := expected["providerCode"]; ok && code != nil && e.ProviderCode != code {
			t.Fatalf("providerCode = %q, want %v", e.ProviderCode, code)
		}
	} else {
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		expectMatch(t, "result", expected, result, true)
	}
	if transport.index != len(transport.steps) {
		t.Fatalf("made %d of %d scripted requests", transport.index, len(transport.steps))
	}
}

// invoke calls the operation with the vector's JSON input turned into the typed request.
func invoke(c *Client, operation string, input map[string]any) (map[string]any, error) {
	ctx := context.Background()
	s := func(key string) string { v, _ := str(input[key]); return v }
	switch operation {
	case "createPayment":
		amount, err := vectorMoney(input["amount"])
		if err != nil {
			return nil, err
		}
		req := CreatePaymentRequest{OrderID: s("orderId"), Amount: amount, ReturnURL: s("returnUrl"), CallbackURL: s("callbackUrl"),
			Description: s("description"), Language: s("language"), Intent: s("intent")}
		req.CustomerEmail, _ = str(get(input, "customer", "email"))
		req.CustomerPhone, _ = str(get(input, "customer", "phone"))
		if m := asMap(input["metadata"]); m != nil {
			req.Metadata = map[string]string{}
			for k, v := range m {
				req.Metadata[k], _ = str(v)
			}
		}
		res, err := c.CreatePayment(ctx, req)
		return res.ToMap(), err
	case "verifyCallback":
		cb := IncomingCallback{Method: s("method"), Query: map[string][]string{}, Header: http.Header{}}
		for k, v := range asMap(input["query"]) {
			cb.Query[k] = vectorStrings(v)
		}
		if body, ok := str(input["body"]); ok {
			cb.Body = body
		} else if fields := asMap(input["body"]); fields != nil {
			cb.Fields = fields
		}
		for k, v := range asMap(input["headers"]) {
			cb.Header[http.CanonicalHeaderKey(k)] = vectorStrings(v)
		}
		res, err := c.VerifyCallback(ctx, cb)
		return res.ToMap(), err
	case "getStatus":
		res, err := c.GetStatus(ctx, s("paymentId"))
		return res.ToMap(), err
	case "refund":
		amount, err := optionalMoney(input["amount"])
		if err != nil {
			return nil, err
		}
		res, err := c.Refund(ctx, RefundRequest{PaymentID: s("paymentId"), Amount: amount, ProviderRef: s("providerRef")})
		return res.ToMap(), err
	case "capture", "cancel":
		amount, err := optionalMoney(input["amount"])
		if err != nil {
			return nil, err
		}
		action := PaymentAction{PaymentID: s("paymentId"), Amount: amount, ProviderRef: s("providerRef")}
		var res PaymentState
		if operation == "capture" {
			res, err = c.Capture(ctx, action)
		} else {
			res, err = c.Cancel(ctx, action)
		}
		return res.ToMap(), err
	}
	return nil, fmt.Errorf("unknown operation %s", operation)
}

// vectorMoney turns {amount, currency} into Money. Money holds an int64, so a fractional amount cannot reach the
// library; it is reported as invalid_request the way the other languages report it.
func vectorMoney(v any) (Money, error) {
	n, ok := get(v, "amount").(json.Number)
	if !ok || strings.ContainsAny(string(n), ".eE") {
		return Money{}, invalid(fmt.Sprintf("amount must be an integer in minor units, got %s", stringify(v)))
	}
	amount, _ := n.Int64()
	currency, _ := str(get(v, "currency"))
	return Money{Amount: amount, Currency: currency}, nil
}

func optionalMoney(v any) (*Money, error) {
	if v == nil {
		return nil, nil
	}
	m, err := vectorMoney(v)
	return &m, err
}

func vectorStrings(v any) []string {
	if s, ok := str(v); ok {
		return []string{s}
	}
	var out []string
	for _, item := range asList(v) {
		if s, ok := str(item); ok {
			out = append(out, s)
		}
	}
	return out
}

func mapStatus(provider string, input map[string]any) (Status, error) {
	s := func(key string) string { v, _ := str(input[key]); return v }
	switch provider {
	case "kapitalbank":
		return KapitalBankStatus(input)
	case "payriff":
		return PayriffStatus(s("paymentStatus"))
	case "epoint":
		return EpointStatus(s("status"))
	case "goldenpay":
		code, _ := integer(get(input, "status", "code"))
		return GoldenPayStatus(code)
	case "yigim":
		return YigimStatus(s("status"))
	}
	return "", fmt.Errorf("%s has no mapStatus", provider)
}

// buildProvider builds the adapter from the vector config, the same way in every language: <name>File is read relative to
// the provider's vector folder, clock (ISO time) and nonce (string) become fixed generators.
func buildProvider(name string, raw map[string]any, doer Doer) (Provider, error) {
	cfg := map[string]string{}
	for k, v := range raw {
		s, _ := str(v)
		if strings.HasSuffix(k, "File") {
			content, err := os.ReadFile(filepath.Join(vectorsDir, name, s))
			if err != nil {
				return nil, err
			}
			cfg[strings.TrimSuffix(k, "File")] = string(content)
		} else {
			cfg[k] = s
		}
	}
	switch name {
	case "kapitalbank":
		return NewKapitalBank(KapitalBankConfig{Username: cfg["username"], Password: cfg["password"], Environment: cfg["environment"], BaseURL: cfg["baseUrl"], HTTPClient: doer})
	case "payriff":
		return NewPayriff(PayriffConfig{SecretKey: cfg["secretKey"], BaseURL: cfg["baseUrl"], HTTPClient: doer})
	case "epoint":
		return NewEpoint(EpointConfig{PublicKey: cfg["publicKey"], PrivateKey: cfg["privateKey"], BaseURL: cfg["baseUrl"], HTTPClient: doer})
	case "goldenpay":
		return NewGoldenPay(GoldenPayConfig{AuthKey: cfg["authKey"], MerchantName: cfg["merchantName"], Environment: cfg["environment"], BaseURL: cfg["baseUrl"], HTTPClient: doer})
	case "yigim":
		return NewYigim(YigimConfig{Merchant: cfg["merchant"], APIKey: cfg["apiKey"], Biller: cfg["biller"], Template: cfg["template"],
			Environment: cfg["environment"], BaseURL: cfg["baseUrl"], HTTPClient: doer})
	case "azericard":
		ac := AzericardConfig{Terminal: cfg["terminal"], PrivateKey: cfg["privateKey"], GatewayPublicKey: cfg["gatewayPublicKey"],
			MerchantName: cfg["merchantName"], MerchantURL: cfg["merchantUrl"], Environment: cfg["environment"], GatewayURL: cfg["gatewayUrl"],
			Email: cfg["email"], Country: cfg["country"], MerchantGMT: cfg["merchantGmt"], HTTPClient: doer}
		if clock := cfg["clock"]; clock != "" {
			at, err := time.Parse(time.RFC3339, clock)
			if err != nil {
				return nil, err
			}
			ac.Clock = func() time.Time { return at }
		}
		if nonce := cfg["nonce"]; nonce != "" {
			ac.Nonce = func() string { return nonce }
		}
		if mac := asMap(raw["macFields"]); mac != nil {
			ac.MacFields = map[string][]string{}
			for k, v := range mac {
				ac.MacFields[k] = vectorStrings(v)
			}
		}
		return NewAzericard(ac)
	}
	return nil, fmt.Errorf("no adapter for %s", name)
}

// scripted serves a vector's http steps in order and checks each request against `expect`.
type scripted struct {
	t     *testing.T
	steps []any
	index int
}

func (s *scripted) Do(req *http.Request) (*http.Response, error) {
	s.index++
	n := s.index
	if n > len(s.steps) {
		s.t.Fatalf("unexpected request #%d: %s %s", n, req.Method, req.URL)
	}
	step := s.steps[n-1]
	expect := asMap(get(step, "expect"))
	if req.Method != expect["method"] {
		s.t.Fatalf("request #%d method %s, want %v", n, req.Method, expect["method"])
	}
	if req.URL.String() != expect["url"] {
		s.t.Fatalf("request #%d url\n got %s\nwant %v", n, req.URL.String(), expect["url"])
	}
	for name, value := range asMap(expect["headers"]) {
		if got := req.Header.Get(name); got != value {
			s.t.Fatalf("request #%d header %s = %q, want %v", n, name, got, value)
		}
	}
	var body string
	if req.Body != nil {
		b, _ := io.ReadAll(req.Body)
		body = string(b)
	}
	if want, ok := expect["json"]; ok {
		got, err := parseJSON(body)
		if err != nil {
			s.t.Fatalf("request #%d body is not JSON: %s", n, body)
		}
		expectMatch(s.t, fmt.Sprintf("request #%d body", n), want, got, false)
	}
	if want, ok := expect["form"]; ok {
		got := map[string]any{}
		for k, v := range formDecode(body) {
			got[k] = v
		}
		expectMatch(s.t, fmt.Sprintf("request #%d form", n), want, got, false)
	}
	respond := asMap(get(step, "respond"))
	text, _ := str(respond["text"])
	if j, ok := respond["json"]; ok {
		text = stringify(j)
	}
	status, _ := integer(respond["status"])
	return &http.Response{StatusCode: int(status), Body: io.NopCloser(strings.NewReader(text)), Header: http.Header{}}, nil
}

// expectMatch compares with the vectors' JSON semantics: numbers by value, a string never equals a number; with
// subset, keys missing from want are ignored (like toMatchObject).
func expectMatch(t *testing.T, path string, want, got any, subset bool) {
	t.Helper()
	got = normalize(got)
	switch w := want.(type) {
	case map[string]any:
		g, ok := got.(map[string]any)
		if !ok {
			t.Fatalf("%s: want an object, got %#v", path, got)
		}
		if !subset && len(g) != len(w) {
			t.Fatalf("%s: keys %v, want %v", path, keysOf(g), keysOf(w))
		}
		for k, v := range w {
			gv, ok := g[k]
			if !ok {
				t.Fatalf("%s.%s is missing", path, k)
			}
			expectMatch(t, path+"."+k, v, gv, subset)
		}
	case []any:
		g, ok := got.([]any)
		if !ok || len(g) != len(w) {
			t.Fatalf("%s: want %d items, got %#v", path, len(w), got)
		}
		for i := range w {
			expectMatch(t, fmt.Sprintf("%s[%d]", path, i), w[i], g[i], subset)
		}
	case json.Number:
		a, _ := new(big.Rat).SetString(string(w))
		b, ok := numberOf(got)
		if !ok || a.Cmp(b) != 0 {
			t.Fatalf("%s: want %s, got %#v", path, w, got)
		}
	default:
		if want != got {
			t.Fatalf("%s: want %#v, got %#v", path, want, got)
		}
	}
}

// normalize turns the library's typed values into plain JSON shapes for comparison.
func normalize(v any) any {
	switch x := v.(type) {
	case map[string]string:
		out := map[string]any{}
		for k, s := range x {
			out[k] = s
		}
		return out
	case map[string][]string:
		out := map[string]any{}
		for k, s := range x {
			out[k] = s
		}
		return out
	case []Field:
		out := map[string]any{}
		for _, f := range x {
			out[f.Name] = f.Value
		}
		return out
	case Status:
		return string(x)
	}
	return v
}

func numberOf(v any) (*big.Rat, bool) {
	switch x := v.(type) {
	case json.Number:
		return new(big.Rat).SetString(string(x))
	case int64:
		return new(big.Rat).SetInt64(x), true
	case int:
		return new(big.Rat).SetInt64(int64(x)), true
	case jsonNumber:
		return new(big.Rat).SetString(string(x))
	}
	return nil, false
}

func keysOf(m map[string]any) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
