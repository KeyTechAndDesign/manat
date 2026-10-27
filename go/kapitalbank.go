package manat

import (
	"context"
	"encoding/base64"
	"fmt"
	"regexp"
	"strings"
	"time"
)

// KapitalBankConfig configures NewKapitalBank. Environment is "test" or "production" (default).
type KapitalBankConfig struct {
	Username    string // API user, such as "TerminalSys/xxxx"
	Password    string
	Environment string
	BaseURL     string
	HTTPClient  Doer
	Timeout     time.Duration
}

// KapitalBank (TXPG REST API).
// The redirect back (?ID=…&STATUS=…) is unsigned, so VerifyCallback re-reads the order.
type KapitalBank struct {
	http    transport
	baseURL string
	auth    string
}

var (
	kbOrderID    = regexp.MustCompile(`^\d+$`)
	kbBareOrigin = regexp.MustCompile(`^[a-zA-Z][a-zA-Z0-9+.-]*://[^/]+$`)
)

const kb = "kapitalbank"

func NewKapitalBank(cfg KapitalBankConfig) (*KapitalBank, error) {
	if cfg.Username == "" || cfg.Password == "" {
		return nil, invalid("kapitalbank: username and password are required")
	}
	base := cfg.BaseURL
	if base == "" {
		base = "https://e-commerce.kapitalbank.az/api"
		if cfg.Environment == "test" {
			base = "https://txpgtst.kapitalbank.az/api"
		}
	}
	return &KapitalBank{
		http:    newTransport(kb, cfg.HTTPClient, cfg.Timeout),
		baseURL: strings.TrimRight(base, "/"),
		auth:    "Basic " + base64.StdEncoding.EncodeToString([]byte(cfg.Username+":"+cfg.Password)),
	}, nil
}

func (p *KapitalBank) Name() string { return kb }

func (p *KapitalBank) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund, CapCapture, CapCancel}
}

// KapitalBankStatus maps an order's status.
func KapitalBankStatus(order map[string]any) (Status, error) {
	status, _ := str(order["status"])
	switch status {
	case "Preparing":
		return StatusPending, nil
	case "Authorized":
		return StatusAuthorized, nil
	case "PartPaid", "FullyPaid", "Funded", "Closed":
		refunded, ok := decimal(order["clearedRefundAmount"])
		if order["clearedRefundAmount"] == nil {
			refunded, ok = decimal(int64(0))
		}
		if ok && refunded.Sign() > 0 {
			if order["clearedChargeAmount"] == nil {
				return StatusRefunded, nil
			}
			charged, ok := decimal(order["clearedChargeAmount"])
			if ok && refunded.Cmp(charged) >= 0 {
				return StatusRefunded, nil
			}
			return StatusPartiallyRefunded, nil
		}
		return StatusPaid, nil
	case "Refunded":
		return StatusRefunded, nil
	case "Cancelled", "Voided", "Expired":
		return StatusCancelled, nil
	case "Rejected", "Refused", "Declined":
		return StatusFailed, nil
	}
	return "", providerError(kb, fmt.Sprintf("kapitalbank: unknown order status %q", status), order)
}

func (p *KapitalBank) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	typeRid := "Order_SMS"
	if req.Intent == IntentAuthorize {
		typeRid = "Order_DMS"
	}
	description := req.Description
	if description == "" {
		description = req.OrderID
	}
	order := object{
		{"typeRid", typeRid},
		{"amount", req.Amount.DecimalString()},
		{"currency", req.Amount.Currency},
		{"language", orDefault(req.Language, "az")},
		{"description", description},
		{"hppRedirectUrl", req.ReturnURL},
	}
	data, err := p.call(ctx, "POST", "/order", object{{"order", order}})
	if err != nil {
		return CreatePaymentResult{}, err
	}
	created := asMap(data["order"])
	if created == nil || !truthy(created["id"]) || !truthy(created["hppUrl"]) || !truthy(created["password"]) {
		return CreatePaymentResult{}, providerError(kb, "kapitalbank: order response is missing id, hppUrl or password", data)
	}
	id := text(created["id"])
	link := withQuery(text(created["hppUrl"]), []pair{{"id", id}, {"password", text(created["password"])}})
	status := StatusPending
	if created["status"] != nil {
		if status, err = KapitalBankStatus(created); err != nil {
			return CreatePaymentResult{}, err
		}
	}
	return CreatePaymentResult{PaymentID: id, Status: status, Next: NextAction{Type: "redirect", URL: link}, Raw: data}, nil
}

func (p *KapitalBank) VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error) {
	id := firstNonEmpty(cb.query("ID"), cb.query("id"), cb.bodyField("ID"))
	if !kbOrderID.MatchString(id) {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "kapitalbank: callback has no numeric ID", Provider: kb, Raw: cb.toMap()}
	}
	// The redirect is unsigned; the API is the source of truth.
	state, err := p.GetStatus(ctx, id)
	if err != nil {
		return PaymentEvent{}, err
	}
	return eventFromState(state), nil
}

func (p *KapitalBank) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	order, err := p.fetchOrder(ctx, paymentID)
	if err != nil {
		return PaymentState{}, err
	}
	return p.toState(order)
}

func (p *KapitalBank) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	amount := req.Amount
	if amount == nil {
		remaining, err := p.remainingRefundable(ctx, req.PaymentID)
		if err != nil {
			return RefundResult{}, err
		}
		amount = &remaining
	}
	data, err := p.exec(ctx, req.PaymentID, object{{"phase", "Single"}, {"amount", amount.DecimalString()}, {"type", "Refund"}})
	if err != nil {
		return RefundResult{}, err
	}
	state, err := p.GetStatus(ctx, req.PaymentID)
	if err != nil {
		return RefundResult{}, err
	}
	refundID := ""
	if actionID := get(data, "tran", "match", "tranActionId"); truthy(actionID) {
		refundID = text(actionID)
	}
	return RefundResult{PaymentID: req.PaymentID, RefundID: refundID, Status: state.Status, Raw: map[string]any{"exec": data, "order": state.Raw}}, nil
}

func (p *KapitalBank) Capture(ctx context.Context, action PaymentAction) (PaymentState, error) {
	amount := action.Amount
	if amount == nil {
		state, err := p.GetStatus(ctx, action.PaymentID)
		if err != nil {
			return PaymentState{}, err
		}
		if state.Amount == nil {
			return PaymentState{}, providerError(kb, "kapitalbank: order "+action.PaymentID+" has no amount to capture", nil)
		}
		amount = state.Amount
	}
	if _, err := p.exec(ctx, action.PaymentID, object{{"phase", "Clearing"}, {"amount", amount.DecimalString()}}); err != nil {
		return PaymentState{}, err
	}
	return p.GetStatus(ctx, action.PaymentID)
}

func (p *KapitalBank) Cancel(ctx context.Context, action PaymentAction) (PaymentState, error) {
	current, err := p.GetStatus(ctx, action.PaymentID)
	if err != nil {
		return PaymentState{}, err
	}
	// Reversal of a pre-auth hold uses phase Auth; a same-day purchase uses phase Single.
	phase := "Single"
	if current.Status == StatusAuthorized {
		phase = "Auth"
	}
	if _, err := p.exec(ctx, action.PaymentID, object{{"phase", phase}, {"voidKind", "Full"}}); err != nil {
		return PaymentState{}, err
	}
	return p.GetStatus(ctx, action.PaymentID)
}

func (p *KapitalBank) remainingRefundable(ctx context.Context, paymentID string) (Money, error) {
	order, err := p.fetchOrder(ctx, paymentID)
	if err != nil {
		return Money{}, err
	}
	currency, err := kbCurrency(order["currency"], paymentID)
	if err != nil {
		return Money{}, err
	}
	charged := order["clearedChargeAmount"]
	if charged == nil {
		charged = order["amount"]
	}
	if charged == nil {
		charged = "0"
	}
	total, err := moneyOf(charged, currency)
	if err != nil {
		return Money{}, err
	}
	remaining := total.Amount
	if truthy(order["clearedRefundAmount"]) {
		refunded, err := moneyOf(order["clearedRefundAmount"], currency)
		if err != nil {
			return Money{}, err
		}
		remaining -= refunded.Amount
	}
	if remaining <= 0 {
		return Money{}, &Error{Code: InvalidRequest, Message: "kapitalbank: order " + paymentID + " has nothing left to refund", Provider: kb, Raw: order}
	}
	return Money{Amount: remaining, Currency: currency}, nil
}

func (p *KapitalBank) fetchOrder(ctx context.Context, paymentID string) (map[string]any, error) {
	if err := kbRequireOrderID(paymentID); err != nil {
		return nil, err
	}
	data, err := p.call(ctx, "GET", "/order/"+paymentID+"?tranDetailLevel=2&tokenDetailLevel=2&orderDetailLevel=2", nil)
	if err != nil {
		return nil, err
	}
	order := asMap(data["order"])
	if order == nil {
		return nil, providerError(kb, "kapitalbank: no order in response for "+paymentID, data)
	}
	if order["id"] == nil {
		order["id"] = paymentID
	}
	return order, nil
}

func (p *KapitalBank) toState(order map[string]any) (PaymentState, error) {
	id := text(order["id"])
	state := PaymentState{PaymentID: id, Raw: order}
	if order["amount"] != nil && order["currency"] != nil {
		currency, err := kbCurrency(order["currency"], id)
		if err != nil {
			return PaymentState{}, err
		}
		amount, err := moneyOf(order["amount"], currency)
		if err != nil {
			return PaymentState{}, err
		}
		state.Amount = &amount
	}
	if truthy(order["clearedRefundAmount"]) && order["currency"] != nil {
		currency, err := kbCurrency(order["currency"], id)
		if err != nil {
			return PaymentState{}, err
		}
		refunded, err := moneyOf(order["clearedRefundAmount"], currency)
		if err != nil {
			return PaymentState{}, err
		}
		state.RefundedAmount = &refunded
	}
	status, err := KapitalBankStatus(order)
	if err != nil {
		return PaymentState{}, err
	}
	state.Status = status
	return state, nil
}

func (p *KapitalBank) exec(ctx context.Context, paymentID string, tran object) (map[string]any, error) {
	if err := kbRequireOrderID(paymentID); err != nil {
		return nil, err
	}
	return p.call(ctx, "POST", "/order/"+paymentID+"/exec-tran", object{{"tran", tran}})
}

func (p *KapitalBank) call(ctx context.Context, method, path string, body any) (map[string]any, error) {
	status, raw, err := p.http.json(ctx, method, p.baseURL+path, []pair{{"authorization", p.auth}}, body, nil)
	if err != nil {
		return nil, err
	}
	data := asMap(raw)
	if status >= 400 || (data != nil && truthy(data["errorCode"])) {
		code, message := text(get(data, "errorCode")), text(get(data, "errorDescription"))
		what := message
		if what == "" {
			what = fmt.Sprintf("HTTP %d", status)
		}
		return nil, &Error{Code: ProviderError, Message: "kapitalbank: " + what, Provider: kb, ProviderCode: code, ProviderMessage: message, Raw: raw}
	}
	if data == nil {
		data = map[string]any{}
	}
	return data, nil
}

func kbRequireOrderID(paymentID string) error {
	if !kbOrderID.MatchString(paymentID) {
		return invalid(fmt.Sprintf("kapitalbank: paymentId must be the numeric order id, got %q", paymentID))
	}
	return nil
}

func kbCurrency(code any, paymentID string) (string, error) {
	c, _ := str(code)
	if !supportedCurrency(c) {
		return "", providerError(kb, fmt.Sprintf("kapitalbank: unexpected currency %q on order %s", text(code), paymentID), nil)
	}
	return c, nil
}

// withQuery sets query parameters on a URL, replacing existing ones with the same name (like URL.searchParams.set).
func withQuery(link string, params []pair) string {
	fragment := ""
	if i := strings.IndexByte(link, '#'); i >= 0 {
		link, fragment = link[:i], link[i:]
	}
	base, query, _ := strings.Cut(link, "?")
	if kbBareOrigin.MatchString(base) {
		base += "/"
	}
	var merged []pair
	seen := map[string]int{}
	for _, part := range strings.Split(query, "&") {
		if part == "" {
			continue
		}
		k, v, _ := strings.Cut(part, "=")
		k = unescape(k)
		if _, ok := seen[k]; !ok {
			seen[k] = len(merged)
			merged = append(merged, pair{k, unescape(v)})
		}
	}
	for _, param := range params {
		if i, ok := seen[param.k]; ok {
			merged[i].v = param.v
		} else {
			seen[param.k] = len(merged)
			merged = append(merged, param)
		}
	}
	return base + "?" + formEncode(merged) + fragment
}

func orDefault(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}
