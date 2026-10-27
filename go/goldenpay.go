package manat

import (
	"context"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// GoldenPayConfig configures NewGoldenPay. Environment is "test" or "production" (default).
type GoldenPayConfig struct {
	AuthKey      string
	MerchantName string
	Environment  string
	BaseURL      string // also used for the payment page
	HTTPClient   Doer
	Timeout      time.Duration
}

// GoldenPay. AZN only.
// The redirect back is unsigned, so VerifyCallback re-reads the payment.
type GoldenPay struct {
	http         transport
	baseURL      string
	authKey      string
	merchantName string
}

const gp = "goldenpay"

var gpKey = regexp.MustCompile(`^[A-Za-z0-9-]{1,64}$`)

// Description limit in bytes.
const gpMaxDescriptionBytes = 500

func NewGoldenPay(cfg GoldenPayConfig) (*GoldenPay, error) {
	if cfg.AuthKey == "" {
		return nil, invalid("goldenpay: authKey is required")
	}
	if cfg.MerchantName == "" {
		return nil, invalid("goldenpay: merchantName is required")
	}
	base := cfg.BaseURL
	if base == "" {
		base = "https://rest-pg.goldenpay.az"
		if cfg.Environment == "test" {
			base = "https://rest-newpgtest.goldenpay.az"
		}
	}
	return &GoldenPay{http: newTransport(gp, cfg.HTTPClient, cfg.Timeout), baseURL: strings.TrimRight(base, "/"),
		authKey: cfg.AuthKey, merchantName: cfg.MerchantName}, nil
}

func (p *GoldenPay) Name() string { return gp }

func (p *GoldenPay) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund}
}

// GoldenPayStatus maps status.code of a getPaymentResult reply.
func GoldenPayStatus(code int64) (Status, error) {
	switch code {
	case 1:
		return StatusPaid, nil
	case 813, 818: // not finished / in progress
		return StatusPending, nil
	case 814, 819: // cancelled by the customer / payment page timed out
		return StatusCancelled, nil
	case 815, 816:
		return StatusFailed, nil
	case 817:
		return StatusRefunded, nil
	}
	return "", providerError(gp, fmt.Sprintf("goldenpay: status code %d does not describe a payment", code), nil)
}

func (p *GoldenPay) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if req.Intent == IntentAuthorize {
		return CreatePaymentResult{}, &Error{Code: UnsupportedOperation, Message: "goldenpay: pre-authorisation is not supported", Provider: gp}
	}
	if err := gpRequireAZN(req.Amount); err != nil {
		return CreatePaymentResult{}, err
	}
	// GoldenPay has no order id field; the description is the only text that reaches its panel.
	description := truncateBytes(orDefault(req.Description, req.OrderID), gpMaxDescriptionBytes)
	amount := strconv.FormatInt(req.Amount.Amount, 10)
	body := object{
		{"merchantName", p.merchantName},
		{"amount", req.Amount.Amount},
		{"lang", orDefault(req.Language, "az")},
		{"description", description},
		{"redirectUrl", req.ReturnURL},
		{"hashCode", md5Hex(p.authKey + p.merchantName + amount + description)},
	}
	res, err := p.call(ctx, "POST", "getPaymentKey", body)
	if err != nil {
		return CreatePaymentResult{}, err
	}
	if code, ok := gpCode(res); !ok || code != 1 || !truthy(res["paymentKey"]) {
		return CreatePaymentResult{}, gpError("payment was not created", res)
	}
	key := text(res["paymentKey"])
	return CreatePaymentResult{PaymentID: key, Status: StatusPending,
		Next: NextAction{Type: "redirect", URL: p.baseURL + "/pay/" + encodeURIComponent(key)}, Raw: res}, nil
}

func (p *GoldenPay) VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error) {
	key := firstNonEmpty(cb.query("payment_key"), cb.query("paymentKey"), cb.bodyField("payment_key"), cb.bodyField("paymentKey"))
	if !gpKey.MatchString(key) {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "goldenpay: redirect has no payment_key", Provider: gp, Raw: cb.toMap()}
	}
	// The redirect is unsigned; trust only what getPaymentResult says about this key.
	state, err := p.GetStatus(ctx, key)
	if err != nil {
		return PaymentEvent{}, err
	}
	return eventFromState(state), nil
}

func (p *GoldenPay) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	if !gpKey.MatchString(paymentID) {
		return PaymentState{}, invalid(fmt.Sprintf("goldenpay: invalid paymentId %q", paymentID))
	}
	query := formEncode([]pair{{"payment_key", paymentID}, {"hash_code", md5Hex(p.authKey + paymentID)}})
	res, err := p.call(ctx, "GET", "getPaymentResult?"+query, nil)
	if err != nil {
		return PaymentState{}, err
	}
	code, ok := gpCode(res)
	if !ok || !gpPaymentCode(code) {
		return PaymentState{}, gpError("status is not available", res)
	}
	if truthy(res["paymentKey"]) && res["paymentKey"] != paymentID {
		return PaymentState{}, gpError(fmt.Sprintf("result is for payment %s, not %s", text(res["paymentKey"]), paymentID), res)
	}
	status, err := GoldenPayStatus(code)
	if err != nil {
		return PaymentState{}, err
	}
	state := PaymentState{PaymentID: paymentID, Status: status, Raw: res}
	if amount, ok := integer(res["amount"]); ok && amount > 0 {
		state.Amount = &Money{Amount: amount, Currency: "AZN"}
	}
	return state, nil
}

func (p *GoldenPay) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	if req.Amount != nil {
		if err := gpRequireAZN(*req.Amount); err != nil {
			return RefundResult{}, err
		}
	}
	// reversePaymentKey needs an amount, and GoldenPay has no partial-refund status, so the payment is read first.
	payment, err := p.GetStatus(ctx, req.PaymentID)
	if err != nil {
		return RefundResult{}, err
	}
	amount := req.Amount
	if amount == nil {
		amount = payment.Amount
	}
	if amount == nil {
		return RefundResult{}, providerError(gp, "goldenpay: payment "+req.PaymentID+" has no amount to refund", payment.Raw)
	}
	hashCode := md5Hex(p.authKey + req.PaymentID)
	res, err := p.call(ctx, "POST", "reversePaymentKey", object{
		{"paymentKey", req.PaymentID},
		{"hashCode", hashCode},
		{"amount", amount.Amount},
		{"secretKey", md5Hex(hashCode + strconv.FormatInt(amount.Amount, 10))},
	})
	if err != nil {
		return RefundResult{}, err
	}
	// 817 (reversed) and 1 both mean success.
	if code, ok := gpCode(res); !ok || (code != 817 && code != 1) {
		return RefundResult{}, gpError("refund failed", res)
	}
	status := StatusRefunded
	if payment.Amount != nil && amount.Amount < payment.Amount.Amount {
		status = StatusPartiallyRefunded
	}
	return RefundResult{PaymentID: req.PaymentID, Status: status, Raw: map[string]any{"refund": res, "payment": payment.Raw}}, nil
}

func (p *GoldenPay) Capture(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported(gp, CapCapture)
}

func (p *GoldenPay) Cancel(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported(gp, CapCancel)
}

func (p *GoldenPay) call(ctx context.Context, method, path string, body any) (map[string]any, error) {
	status, raw, err := p.http.json(ctx, method, p.baseURL+"/"+path, nil, body, nil)
	if err != nil {
		return nil, err
	}
	data := asMap(raw)
	if data == nil || !truthy(data["status"]) {
		return nil, providerError(gp, fmt.Sprintf("goldenpay: unexpected response (HTTP %d)", status), raw)
	}
	if status >= 400 {
		return nil, gpError(fmt.Sprintf("HTTP %d", status), data)
	}
	return data, nil
}

func gpCode(res map[string]any) (int64, bool) {
	return integer(get(res, "status", "code"))
}

func gpPaymentCode(code int64) bool {
	switch code {
	case 1, 813, 814, 815, 816, 817, 818, 819:
		return true
	}
	return false
}

func gpError(what string, res map[string]any) *Error {
	raw := get(res, "status", "code")
	message := text(get(res, "status", "message"))
	// Code 1 is GoldenPay saying "success" (e.g. a result for another key), so only `what` describes the problem.
	code, isInt := integer(raw)
	fromProvider := raw != nil && !(isInt && code == 1)
	e := &Error{Code: ProviderError, Message: "goldenpay: " + what, Provider: gp, Raw: res}
	if fromProvider {
		e.ProviderCode = text(raw)
		if message != "" {
			e.Message = "goldenpay: " + message
			e.ProviderMessage = message
		}
	}
	return e
}

func gpRequireAZN(m Money) error {
	if m.Currency != "AZN" {
		return invalid("goldenpay: only AZN is supported, got " + m.Currency)
	}
	return nil
}

// truncateBytes cuts to at most max UTF-8 bytes without splitting a character.
func truncateBytes(s string, max int) string {
	if len(s) <= max {
		return s
	}
	end := 0
	for i := range s {
		if i > max {
			break
		}
		end = i
	}
	return s[:end]
}

// encodeURIComponent is JavaScript's encodeURIComponent.
func encodeURIComponent(s string) string {
	e := url.QueryEscape(s)
	e = strings.ReplaceAll(e, "+", "%20")
	for _, c := range []string{"!", "'", "(", ")", "*", "~"} {
		e = strings.ReplaceAll(e, url.QueryEscape(c), c)
	}
	return e
}
