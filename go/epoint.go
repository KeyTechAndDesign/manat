package manat

import (
	"context"
	"crypto/sha1"
	"encoding/base64"
	"fmt"
	"regexp"
	"strings"
	"time"
)

// EpointConfig configures NewEpoint.
type EpointConfig struct {
	PublicKey  string // merchant public key (i0000000000)
	PrivateKey string // signs requests and checks callbacks
	BaseURL    string
	HTTPClient Doer
	Timeout    time.Duration
}

// Epoint (API v1).
// Every message is data (base64 JSON) + signature = base64(sha1(privateKey + data + privateKey)).
type Epoint struct {
	http       transport
	baseURL    string
	publicKey  string
	privateKey string
}

const ep = "epoint"

var epBase64 = regexp.MustCompile(`^[A-Za-z0-9+/]+={0,2}$`)

func NewEpoint(cfg EpointConfig) (*Epoint, error) {
	if cfg.PublicKey == "" {
		return nil, invalid("epoint: publicKey is required")
	}
	if cfg.PrivateKey == "" {
		return nil, invalid("epoint: privateKey is required")
	}
	return &Epoint{
		http:       newTransport(ep, cfg.HTTPClient, cfg.Timeout),
		baseURL:    strings.TrimRight(orDefault(cfg.BaseURL, "https://epoint.az/api/1"), "/"),
		publicKey:  cfg.PublicKey,
		privateKey: cfg.PrivateKey,
	}, nil
}

func (p *Epoint) Name() string { return ep }

func (p *Epoint) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund}
}

// EpointSignature is base64(sha1(privateKey + data + privateKey)).
func EpointSignature(privateKey, data string) string {
	sum := sha1.Sum([]byte(privateKey + data + privateKey))
	return base64.StdEncoding.EncodeToString(sum[:])
}

// EpointEncode turns JSON text into the data and signature fields Epoint expects.
func EpointEncode(privateKey, jsonText string) (data, signature string) {
	data = base64.StdEncoding.EncodeToString([]byte(jsonText))
	return data, EpointSignature(privateKey, data)
}

// EpointStatus maps a payment status.
func EpointStatus(status string) (Status, error) {
	switch status {
	case "new":
		return StatusPending, nil
	case "success":
		return StatusPaid, nil
	case "returned":
		return StatusRefunded, nil
	case "failed", "error":
		return StatusFailed, nil
	}
	return "", providerError(ep, fmt.Sprintf("epoint: unknown status %q", status), nil)
}

func (p *Epoint) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if req.Intent == IntentAuthorize {
		return CreatePaymentResult{}, &Error{Code: UnsupportedOperation, Provider: ep,
			Message: "epoint: pre-authorisation is not supported (Epoint statuses do not tell a hold from a capture)"}
	}
	switch req.Amount.Currency {
	case "AZN", "USD", "EUR", "RUB":
	default:
		return CreatePaymentResult{}, invalid("epoint: currency " + req.Amount.Currency + " is not supported (AZN, USD, EUR, RUB)")
	}
	if len(req.OrderID) > 255 {
		return CreatePaymentResult{}, invalid("epoint: orderId is longer than 255 characters")
	}
	payload := object{
		{"public_key", p.publicKey},
		{"amount", req.Amount.DecimalString()},
		{"currency", req.Amount.Currency},
		{"language", orDefault(req.Language, "az")},
		{"order_id", req.OrderID},
	}
	if req.Description != "" {
		payload = append(payload, kv{"description", truncate(req.Description, 1000)})
	}
	payload = append(payload, kv{"success_redirect_url", req.ReturnURL}, kv{"error_redirect_url", req.ReturnURL})
	res, err := p.call(ctx, "request", payload)
	if err != nil {
		return CreatePaymentResult{}, err
	}
	if res["status"] != "success" || !truthy(res["redirect_url"]) || !truthy(res["transaction"]) {
		return CreatePaymentResult{}, epointError("payment was not created", res)
	}
	return CreatePaymentResult{PaymentID: text(res["transaction"]), Status: StatusPending,
		Next: NextAction{Type: "redirect", URL: text(res["redirect_url"])}, Raw: res}, nil
}

func (p *Epoint) VerifyCallback(_ context.Context, cb IncomingCallback) (PaymentEvent, error) {
	data, signature := cb.bodyField("data"), cb.bodyField("signature")
	// An empty data still has a valid signature (sha1(key + key)), so it is rejected outright.
	if data == "" || signature == "" {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Provider: ep, Raw: cb.bodyValue(),
			Message: "epoint: callback has no data/signature (the browser redirect is not signed; use GetStatus)"}
	}
	if !safeEqual(signature, EpointSignature(p.privateKey, data)) {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "epoint: callback signature does not match", Provider: ep, Raw: cb.bodyValue()}
	}
	if !epBase64.MatchString(data) {
		return PaymentEvent{}, providerError(ep, "epoint: callback data is not base64", data)
	}
	decoded, err := base64.StdEncoding.DecodeString(data)
	if err != nil {
		return PaymentEvent{}, providerError(ep, "epoint: callback data is not base64", data)
	}
	parsed, err := parseJSON(string(decoded))
	if err != nil {
		return PaymentEvent{}, providerError(ep, "epoint: callback data is not JSON", data)
	}
	payload := asMap(parsed)
	if payload == nil || !truthy(payload["transaction"]) {
		return PaymentEvent{}, providerError(ep, "epoint: signed callback has no transaction", parsed)
	}
	s, _ := str(payload["status"])
	status, err := EpointStatus(s)
	if err != nil {
		return PaymentEvent{}, err
	}
	event := PaymentEvent{PaymentID: text(payload["transaction"]), Status: status, Raw: payload}
	if truthy(payload["order_id"]) {
		event.OrderID = text(payload["order_id"])
	}
	return event, nil
}

func (p *Epoint) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	res, err := p.call(ctx, "get-status", object{{"public_key", p.publicKey}, {"transaction", paymentID}})
	if err != nil {
		return PaymentState{}, err
	}
	// server_error means Epoint could not check the payment with the bank; the outcome is unknown.
	if res["status"] == "server_error" || !truthy(res["transaction"]) {
		return PaymentState{}, epointError("status is not available", res)
	}
	if res["transaction"] != paymentID {
		return PaymentState{}, epointError(fmt.Sprintf("status is for transaction %s, not %s", text(res["transaction"]), paymentID), res)
	}
	s, _ := str(res["status"])
	status, err := EpointStatus(s)
	if err != nil {
		return PaymentState{}, err
	}
	return PaymentState{PaymentID: paymentID, Status: status, Raw: res}, nil
}

func (p *Epoint) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	payload := object{{"public_key", p.publicKey}, {"language", "az"}, {"transaction", req.PaymentID}}
	currency := "AZN"
	if req.Amount != nil {
		payload = append(payload, kv{"amount", req.Amount.DecimalString()})
		currency = req.Amount.Currency
	}
	payload = append(payload, kv{"currency", currency})
	res, err := p.call(ctx, "reverse", payload)
	if err != nil {
		return RefundResult{}, err
	}
	if res["status"] != "success" {
		return RefundResult{}, epointError("refund failed", res)
	}
	state, err := p.GetStatus(ctx, req.PaymentID)
	if err != nil {
		return RefundResult{}, err
	}
	// Epoint has no partial-refund status: a reversed payment still reported as "success" was partly refunded.
	status := state.Status
	if status == StatusPaid {
		status = StatusPartiallyRefunded
	}
	return RefundResult{PaymentID: req.PaymentID, Status: status, Raw: map[string]any{"refund": res, "payment": state.Raw}}, nil
}

func (p *Epoint) Capture(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported(ep, CapCapture)
}

func (p *Epoint) Cancel(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported(ep, CapCancel)
}

func (p *Epoint) call(ctx context.Context, method string, payload object) (map[string]any, error) {
	data, signature := EpointEncode(p.privateKey, stringify(payload))
	status, raw, err := p.http.json(ctx, "POST", p.baseURL+"/"+method, nil, nil, []pair{{"data", data}, {"signature", signature}})
	if err != nil {
		return nil, err
	}
	body := asMap(raw)
	if body == nil {
		return nil, providerError(ep, fmt.Sprintf("epoint: unexpected response (HTTP %d)", status), raw)
	}
	if status >= 400 {
		return nil, epointError(fmt.Sprintf("HTTP %d", status), body)
	}
	return body, nil
}

func epointError(what string, res map[string]any) *Error {
	message := what
	if res["message"] != nil {
		message = text(res["message"])
	}
	e := &Error{Code: ProviderError, Message: "epoint: " + message, Provider: ep, Raw: res}
	if truthy(res["code"]) {
		e.ProviderCode = text(res["code"])
	}
	if truthy(res["message"]) {
		e.ProviderMessage = text(res["message"])
	}
	return e
}
