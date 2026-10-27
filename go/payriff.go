package manat

import (
	"context"
	"fmt"
	"regexp"
	"sort"
	"strings"
	"time"
)

// PayriffConfig configures NewPayriff.
type PayriffConfig struct {
	SecretKey  string // application secret key; a "Development" application's key is test mode
	BaseURL    string
	HTTPClient Doer
	Timeout    time.Duration
}

// Payriff (API v3).
// The webhook is unsigned, so VerifyCallback re-reads the order. There is no void for a pre-auth.
type Payriff struct {
	http      transport
	baseURL   string
	secretKey string
}

const pr = "payriff"

var prOrderID = regexp.MustCompile(`^[A-Za-z0-9-]{1,64}$`)

func NewPayriff(cfg PayriffConfig) (*Payriff, error) {
	if cfg.SecretKey == "" {
		return nil, invalid("payriff: secretKey is required")
	}
	return &Payriff{
		http:      newTransport(pr, cfg.HTTPClient, cfg.Timeout),
		baseURL:   strings.TrimRight(orDefault(cfg.BaseURL, "https://api.payriff.com"), "/"),
		secretKey: cfg.SecretKey,
	}, nil
}

func (p *Payriff) Name() string { return pr }

func (p *Payriff) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund, CapCapture}
}

// PayriffStatus maps payload.paymentStatus.
func PayriffStatus(status string) (Status, error) {
	switch status {
	case "CREATED", "PENDING", "ACCEPTED", "IN_REVIEW":
		return StatusPending, nil
	case "APPROVED", "PAID", "PARTIAL", "CASH": // PARTIAL: pre-auth partially captured
		return StatusPaid, nil
	case "PREAUTH_APPROVED":
		return StatusAuthorized, nil
	case "REFUNDED", "REFUND_IN_PROGRESS":
		return StatusRefunded, nil
	case "PARTIAL_REFUND":
		return StatusPartiallyRefunded, nil
	case "CANCELED", "EXPIRED", "REVERSE", "PREAUTH_EXPIRED":
		return StatusCancelled, nil
	case "DECLINED":
		return StatusFailed, nil
	}
	return "", providerError(pr, fmt.Sprintf("payriff: unknown paymentStatus %q", status), nil)
}

func (p *Payriff) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if req.CallbackURL != "" && !strings.HasPrefix(req.CallbackURL, "https://") {
		return CreatePaymentResult{}, invalid("payriff: callbackUrl must be an HTTPS URL")
	}
	operation := "PURCHASE"
	if req.Intent == IntentAuthorize {
		operation = "PRE_AUTH"
	}
	description := req.Description
	if description == "" {
		description = "Order " + req.OrderID
	}
	body := object{
		{"amount", req.Amount.number()},
		{"currency", req.Amount.Currency},
		{"language", strings.ToUpper(orDefault(req.Language, "az"))},
		{"operation", operation},
		{"description", description},
		{"redirectUrl", req.ReturnURL},
	}
	if req.CallbackURL != "" {
		body = append(body, kv{"callbackUrl", req.CallbackURL})
	}
	if len(req.Metadata) > 0 {
		keys := make([]string, 0, len(req.Metadata))
		for k := range req.Metadata {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		metadata := object{}
		for _, k := range keys {
			metadata = append(metadata, kv{k, req.Metadata[k]})
		}
		body = append(body, kv{"metadata", metadata})
	}
	envelope, err := p.call(ctx, "POST", "/api/v3/orders", body)
	if err != nil {
		return CreatePaymentResult{}, err
	}
	payload := asMap(envelope["payload"])
	if payload == nil || !truthy(payload["orderId"]) || !truthy(payload["paymentUrl"]) {
		return CreatePaymentResult{}, providerError(pr, "payriff: order response has no orderId or paymentUrl", envelope["payload"])
	}
	return CreatePaymentResult{PaymentID: text(payload["orderId"]), Status: StatusPending,
		Next: NextAction{Type: "redirect", URL: text(payload["paymentUrl"])}, Raw: payload}, nil
}

func (p *Payriff) VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error) {
	var body any = cb.Fields
	if cb.Body != "" {
		body, _ = parseJSON(cb.Body)
	}
	orderID, ok := str(get(body, "payload", "orderId"))
	if !ok {
		orderID = cb.query("orderId")
	}
	if orderID == "" {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "payriff: callback has no payload.orderId", Provider: pr, Raw: cb.bodyValue()}
	}
	// The webhook is unsigned; trust only what the API says about this order.
	state, err := p.GetStatus(ctx, orderID)
	if err != nil {
		return PaymentEvent{}, err
	}
	return eventFromState(state), nil
}

func (p *Payriff) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	if !prOrderID.MatchString(paymentID) {
		return PaymentState{}, invalid(fmt.Sprintf("payriff: invalid paymentId %q", paymentID))
	}
	envelope, err := p.call(ctx, "GET", "/api/v3/orders/"+paymentID, nil)
	if err != nil {
		return PaymentState{}, err
	}
	order := asMap(envelope["payload"])
	if order == nil || !truthy(order["orderId"]) {
		return PaymentState{}, providerError(pr, "payriff: no order in response for "+paymentID, envelope["payload"])
	}
	state := PaymentState{PaymentID: text(order["orderId"]), Raw: order}
	if order["amount"] != nil && truthy(order["currencyType"]) {
		currency, _ := str(order["currencyType"])
		if !supportedCurrency(currency) {
			return PaymentState{}, providerError(pr, fmt.Sprintf("payriff: unsupported currency %q", text(order["currencyType"])), nil)
		}
		amount, err := moneyOf(order["amount"], currency)
		if err != nil {
			return PaymentState{}, err
		}
		state.Amount = &amount
	}
	if state.Status, err = payriffOrderStatus(order); err != nil {
		return PaymentState{}, err
	}
	return state, nil
}

func (p *Payriff) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	// Payriff rejects a refund without an amount ("Request refund amount is null"), so a full refund sends the order amount.
	amount := req.Amount
	if amount == nil {
		state, err := p.GetStatus(ctx, req.PaymentID)
		if err != nil {
			return RefundResult{}, err
		}
		if state.Amount == nil {
			return RefundResult{}, providerError(pr, "payriff: order "+req.PaymentID+" has no amount to refund", nil)
		}
		amount = state.Amount
	}
	envelope, err := p.call(ctx, "POST", "/api/v3/refund", object{{"orderId", req.PaymentID}, {"refundReason", "Refund"}, {"amount", amount.number()}})
	if err != nil {
		return RefundResult{}, err
	}
	state, err := p.GetStatus(ctx, req.PaymentID)
	if err != nil {
		return RefundResult{}, err
	}
	refundID := ""
	if truthy(envelope["responseId"]) {
		refundID = text(envelope["responseId"])
	}
	return RefundResult{PaymentID: req.PaymentID, RefundID: refundID, Status: state.Status, Raw: map[string]any{"refund": envelope, "order": state.Raw}}, nil
}

func (p *Payriff) Capture(ctx context.Context, action PaymentAction) (PaymentState, error) {
	body := object{{"orderId", action.PaymentID}}
	if action.Amount != nil {
		body = append(body, kv{"amount", action.Amount.number()})
	}
	if _, err := p.call(ctx, "POST", "/api/v3/complete", body); err != nil {
		return PaymentState{}, err
	}
	return p.GetStatus(ctx, action.PaymentID)
}

func (p *Payriff) Cancel(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported(pr, CapCancel)
}

// call returns the whole envelope.
func (p *Payriff) call(ctx context.Context, method, path string, body any) (map[string]any, error) {
	status, raw, err := p.http.json(ctx, method, p.baseURL+path, []pair{{"authorization", p.secretKey}}, body, nil)
	if err != nil {
		return nil, err
	}
	data := asMap(raw)
	if data == nil {
		return nil, providerError(pr, fmt.Sprintf("payriff: unexpected response (HTTP %d)", status), raw)
	}
	code, hasCode := data["code"]
	if !hasCode {
		return nil, providerError(pr, fmt.Sprintf("payriff: unexpected response (HTTP %d)", status), raw)
	}
	if status >= 400 || code != "00000" {
		what := "code " + text(code)
		if data["message"] != nil {
			what = text(data["message"])
		}
		e := &Error{Code: ProviderError, Message: "payriff: " + what, Provider: pr, Raw: data}
		if truthy(code) {
			e.ProviderCode = text(code)
		}
		if truthy(data["message"]) {
			e.ProviderMessage = text(data["message"])
		}
		return nil, e
	}
	return data, nil
}

// payriffOrderStatus: after a partial refund Payriff reports the order as REFUNDED; only its transaction says PARTIAL_REFUND.
func payriffOrderStatus(order map[string]any) (Status, error) {
	s, _ := str(order["paymentStatus"])
	status, err := PayriffStatus(s)
	if err != nil {
		return "", err
	}
	if status == StatusRefunded {
		for _, t := range asList(order["transactions"]) {
			if get(t, "status") == "PARTIAL_REFUND" {
				return StatusPartiallyRefunded, nil
			}
		}
	}
	return status, nil
}
