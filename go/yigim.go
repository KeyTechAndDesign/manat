package manat

import (
	"context"
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// YigimConfig configures NewYigim. Environment is "test" or "production" (default); BaseURL overrides both.
type YigimConfig struct {
	Merchant    string // X-Merchant
	APIKey      string // X-API-Key
	Biller      string
	Template    string // payment page template; default TPL0001
	Environment string
	BaseURL     string
	HTTPClient  Doer
	Timeout     time.Duration
}

// Yigim (MAGNET).
// The webhook carries only `reference` and is unsigned, so VerifyCallback re-reads the payment.
// 09 (issuer timeout) and S7 are not final: the money may have moved, so they stay pending until cancelled.
type Yigim struct {
	http     transport
	baseURL  string
	merchant string
	apiKey   string
	biller   string
	template string
}

const yg = "yigim"

var (
	ygReference = regexp.MustCompile(`^[^\s\x00-\x1f]{1,255}$`)
	ygCode      = regexp.MustCompile(`^-?\d+$`)
)

func NewYigim(cfg YigimConfig) (*Yigim, error) {
	if cfg.Merchant == "" {
		return nil, invalid("yigim: merchant is required")
	}
	if cfg.APIKey == "" {
		return nil, invalid("yigim: apiKey is required")
	}
	if cfg.Biller == "" {
		return nil, invalid("yigim: biller is required")
	}
	base := cfg.BaseURL
	if base == "" {
		base = "https://api.pay.yigim.az"
		if cfg.Environment == "test" {
			base = "https://sandbox.api.pay.yigim.az"
		}
	}
	return &Yigim{http: newTransport(yg, cfg.HTTPClient, cfg.Timeout), baseURL: strings.TrimRight(base, "/"), merchant: cfg.Merchant,
		apiKey: cfg.APIKey, biller: cfg.Biller, template: orDefault(cfg.Template, "TPL0001")}, nil
}

func (p *Yigim) Name() string { return yg }

func (p *Yigim) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund, CapCapture, CapCancel}
}

// YigimStatus maps a payment status code.
func YigimStatus(status string) (Status, error) {
	switch status {
	case "S0", "S2", "S7", "09": // waiting for card data, in progress, malfunction, issuer timeout
		return StatusPending, nil
	case "S1": // pre-authorised DMS payment: charge or cancel within 30 days
		return StatusAuthorized, nil
	case "00":
		return StatusPaid, nil
	case "S4": // reversed
		return StatusCancelled, nil
	case "S3", "01", "02", "03", "04", "05", "06", "07", "08": // unknown error, declines
		return StatusFailed, nil
	}
	return "", providerError(yg, fmt.Sprintf("yigim: unknown payment status %q", status), nil)
}

func (p *Yigim) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if err := ygRequireReference(req.OrderID); err != nil {
		return CreatePaymentResult{}, err
	}
	// The return URL travels in `extra` (name=value;name=value), which the gateway also cuts at `&`.
	if strings.ContainsAny(req.ReturnURL, "&;") {
		return CreatePaymentResult{}, invalid(`yigim: returnUrl may carry at most one query parameter and no ";"`)
	}
	kind := "SMS"
	if req.Intent == IntentAuthorize {
		kind = "DMS"
	}
	params := []pair{
		{"reference", req.OrderID},
		{"type", kind},
		{"amount", strconv.FormatInt(req.Amount.Amount, 10)},
		{"currency", req.Amount.NumericCurrency()},
		{"biller", p.biller},
	}
	if req.Description != "" {
		params = append(params, pair{"description", req.Description})
	}
	params = append(params, pair{"template", p.template}, pair{"language", orDefault(req.Language, "az")})
	// The gateway appends `&reference=…` to the callback URL as is, so it needs a query string.
	if req.CallbackURL != "" {
		callback := req.CallbackURL
		if !strings.Contains(callback, "?") {
			callback += "?"
		}
		params = append(params, pair{"callback", callback})
	}
	params = append(params, pair{"extra", "back-url=" + req.ReturnURL + ";fail-url=" + req.ReturnURL})
	reply, err := p.call(ctx, "payment/create", params)
	if err != nil {
		return CreatePaymentResult{}, err
	}
	link, _ := str(reply["url"])
	if link == "" {
		return CreatePaymentResult{}, ygError("payment page URL is missing", reply)
	}
	return CreatePaymentResult{PaymentID: req.OrderID, Status: StatusPending, Next: NextAction{Type: "redirect", URL: link}, Raw: reply}, nil
}

func (p *Yigim) VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error) {
	reference := firstNonEmpty(cb.query("reference"), cb.bodyField("reference"))
	if !ygReference.MatchString(reference) {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "yigim: callback has no reference", Provider: yg, Raw: cb.toMap()}
	}
	// The webhook is unsigned; trust only what payment/status says about this reference.
	state, err := p.GetStatus(ctx, reference)
	if err != nil {
		return PaymentEvent{}, err
	}
	event := eventFromState(state)
	event.OrderID = state.PaymentID
	return event, nil
}

func (p *Yigim) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	if err := ygRequireReference(paymentID); err != nil {
		return PaymentState{}, err
	}
	reply, err := p.call(ctx, "payment/status", []pair{{"reference", paymentID}})
	if err != nil {
		return PaymentState{}, err
	}
	if truthy(reply["reference"]) && reply["reference"] != paymentID {
		return PaymentState{}, ygError(fmt.Sprintf("status is for payment %s, not %s", text(reply["reference"]), paymentID), reply)
	}
	status, err := YigimStatus(text(reply["status"]))
	if err != nil {
		return PaymentState{}, err
	}
	state := PaymentState{PaymentID: paymentID, Status: status, Raw: reply}
	numeric, _ := str(reply["currency"])
	if currency := currencyFromNumeric(numeric); currency != "" && reply["amount"] != nil && reply["amount"] != "" {
		amount, err := moneyOf(reply["amount"], currency)
		if err != nil {
			return PaymentState{}, err
		}
		state.Amount = &amount
	}
	return state, nil
}

func (p *Yigim) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	reply, err := p.call(ctx, "payment/refund", ygWithAmount(req.PaymentID, req.Amount))
	if err != nil {
		return RefundResult{}, err
	}
	state, err := p.GetStatus(ctx, req.PaymentID)
	if err != nil {
		return RefundResult{}, err
	}
	// While payment/status still reads 00 the requested amount decides.
	status := state.Status
	if status == StatusPaid {
		status = StatusRefunded
		if req.Amount != nil && state.Amount != nil && req.Amount.Amount < state.Amount.Amount {
			status = StatusPartiallyRefunded
		}
	}
	return RefundResult{PaymentID: req.PaymentID, Status: status, Raw: map[string]any{"refund": reply, "payment": state.Raw}}, nil
}

func (p *Yigim) Capture(ctx context.Context, action PaymentAction) (PaymentState, error) {
	if _, err := p.call(ctx, "payment/charge", ygWithAmount(action.PaymentID, action.Amount)); err != nil {
		return PaymentState{}, err
	}
	return p.GetStatus(ctx, action.PaymentID)
}

func (p *Yigim) Cancel(ctx context.Context, action PaymentAction) (PaymentState, error) {
	if _, err := p.call(ctx, "payment/cancel", []pair{{"reference", action.PaymentID}}); err != nil {
		return PaymentState{}, err
	}
	return p.GetStatus(ctx, action.PaymentID)
}

func (p *Yigim) call(ctx context.Context, command string, params []pair) (map[string]any, error) {
	if err := ygRequireReference(params[0].v); err != nil {
		return nil, err
	}
	headers := []pair{{"x-merchant", p.merchant}, {"x-api-key", p.apiKey}, {"x-type", "JSON"}}
	status, raw, err := p.http.json(ctx, "GET", p.baseURL+"/"+command+"?"+formEncode(params), headers, nil, nil)
	if err != nil {
		return nil, err
	}
	// Replies may be nested under `response`.
	reply := asMap(raw)
	if inner := asMap(reply["response"]); inner != nil {
		reply = inner
	}
	code, ok := ygReplyCode(reply)
	if reply == nil || !ok {
		return nil, providerError(yg, fmt.Sprintf("yigim: unexpected response (HTTP %d)", status), raw)
	}
	if status >= 400 || code != 0 {
		return nil, ygError(fmt.Sprintf("HTTP %d", status), reply)
	}
	return reply, nil
}

// ygReplyCode reads `code` as 0 or "0".
func ygReplyCode(reply map[string]any) (int64, bool) {
	if s, ok := str(reply["code"]); ok {
		if !ygCode.MatchString(s) {
			return 0, false
		}
		n, err := strconv.ParseInt(s, 10, 64)
		return n, err == nil
	}
	return integer(reply["code"])
}

func ygError(what string, reply map[string]any) *Error {
	e := &Error{Code: ProviderError, Message: "yigim: " + what, Provider: yg, Raw: reply}
	if code, ok := ygReplyCode(reply); ok && code != 0 {
		e.ProviderCode = strconv.FormatInt(code, 10)
		if message := text(reply["message"]); message != "" {
			e.Message = "yigim: " + message
			e.ProviderMessage = message
		}
	}
	return e
}

func ygWithAmount(reference string, amount *Money) []pair {
	if amount == nil {
		return []pair{{"reference", reference}}
	}
	return []pair{{"reference", reference}, {"amount", strconv.FormatInt(amount.Amount, 10)}}
}

func ygRequireReference(reference string) error {
	if !ygReference.MatchString(reference) {
		return invalid(fmt.Sprintf("yigim: invalid reference %q", reference))
	}
	return nil
}

var (
	_ Provider = (*KapitalBank)(nil)
	_ Provider = (*Azericard)(nil)
	_ Provider = (*Payriff)(nil)
	_ Provider = (*Epoint)(nil)
	_ Provider = (*GoldenPay)(nil)
	_ Provider = (*Yigim)(nil)
)
