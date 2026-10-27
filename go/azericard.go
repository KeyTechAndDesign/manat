package manat

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/hex"
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// AzericardConfig configures NewAzericard. PrivateKey and GatewayPublicKey are PEM text; GatewayPublicKey is needed to
// verify callbacks. Clock and Nonce are for tests.
type AzericardConfig struct {
	Terminal         string
	PrivateKey       string
	GatewayPublicKey string
	MerchantName     string
	MerchantURL      string
	Environment      string // "test" or "production" (default)
	GatewayURL       string
	Email            string
	Country          string
	MerchantGMT      string
	MacFields        map[string][]string // field orders that differ on your terminal, by message type
	Clock            func() time.Time
	Nonce            func() string
	HTTPClient       Doer
	Timeout          time.Duration
}

// Azericard (form POST, RSA-signed).
// Each message is signed over the length-prefixed values of a fixed field list (MacFields).
// The callback signature does not cover ORDER or ACTION, so only a signed APPROVAL counts as approved.
type Azericard struct {
	http       transport
	url        string
	cfg        AzericardConfig
	privateKey *rsa.PrivateKey
	gatewayKey *rsa.PublicKey
	macFields  map[string][]string
}

const az = "azericard"

// AzericardMacFields are the MAC field orders per message type.
var AzericardMacFields = map[string][]string{
	"purchase":   {"AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE", "MERCH_URL"},
	"completion": {"AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"},
	"reversal":   {"AMOUNT", "CURRENCY", "TERMINAL", "TRTYPE", "ORDER", "RRN", "INT_REF"},
	"callback":   {"AMOUNT", "TERMINAL", "APPROVAL", "RRN", "INT_REF"},
	"status":     {"ORDER", "TERMINAL", "TRTYPE", "TIMESTAMP", "NONCE"},
}

var azActionMessages = map[string]string{
	"0": "Approved",
	"1": "Duplicate transaction",
	"2": "Transaction declined",
	"3": "Transaction processing error",
	"6": "Repeat of a declined transaction",
	"7": "Repeat of a transaction with an authentication error",
	"8": "Repeat of a transaction that terminated without a response",
}

var (
	azOrder       = regexp.MustCompile(`^\d{6,32}$`)
	azHex         = regexp.MustCompile(`^[0-9a-fA-F]+$`)
	azThreeDigits = regexp.MustCompile(`^\d{3}$`)
	azBakuTime    = regexp.MustCompile(`^(\d{4})\.(\d{2})\.(\d{2}) (\d{2}):(\d{2}):(\d{2})$`)
)

func NewAzericard(cfg AzericardConfig) (*Azericard, error) {
	if cfg.Terminal == "" {
		return nil, invalid("azericard: terminal is required")
	}
	if cfg.MerchantName == "" || cfg.MerchantURL == "" {
		return nil, invalid("azericard: merchantName and merchantUrl are required")
	}
	if cfg.PrivateKey == "" {
		return nil, invalid("azericard: privateKey (PEM) is required")
	}
	key, err := rsaPrivateKey(cfg.PrivateKey)
	if err != nil {
		return nil, &Error{Code: InvalidRequest, Message: "azericard: privateKey is not a valid PEM key", Provider: az, Err: err}
	}
	if key.N.BitLen() < 2048 {
		return nil, invalid("azericard: privateKey must be an RSA key of at least 2048 bits")
	}
	p := &Azericard{http: newTransport(az, cfg.HTTPClient, cfg.Timeout), cfg: cfg, privateKey: key, macFields: map[string][]string{}}
	if cfg.GatewayPublicKey != "" {
		if p.gatewayKey, err = rsaPublicKey(cfg.GatewayPublicKey); err != nil {
			return nil, &Error{Code: InvalidRequest, Message: "azericard: gatewayPublicKey is not a valid PEM key", Provider: az, Err: err}
		}
	}
	p.url = cfg.GatewayURL
	if p.url == "" {
		p.url = "https://mpi.3dsecure.az/cgi-bin/cgi_link"
		if cfg.Environment == "test" {
			p.url = "https://testmpi.3dsecure.az/cgi-bin/cgi_link"
		}
	}
	for k, v := range AzericardMacFields {
		p.macFields[k] = v
	}
	for k, v := range cfg.MacFields {
		p.macFields[k] = v
	}
	if p.cfg.Clock == nil {
		p.cfg.Clock = time.Now
	}
	if p.cfg.Nonce == nil {
		p.cfg.Nonce = func() string {
			b := make([]byte, 16)
			_, _ = rand.Read(b)
			return hex.EncodeToString(b)
		}
	}
	return p, nil
}

func (p *Azericard) Name() string { return az }

func (p *Azericard) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus, CapRefund, CapCapture, CapCancel}
}

// AzericardMacSource is the length-prefixed MAC source: "11.48" → "511.48", empty → "-". Lengths are in bytes.
func AzericardMacSource(fields map[string]string, order []string) string {
	var b strings.Builder
	for _, name := range order {
		if v := fields[name]; v == "" {
			b.WriteByte('-')
		} else {
			b.WriteString(strconv.Itoa(len(v)))
			b.WriteString(v)
		}
	}
	return b.String()
}

// AzericardTimestamp is the GMT timestamp YYYYMMDDHHMMSS.
func AzericardTimestamp(t time.Time) string {
	return t.UTC().Format("20060102150405")
}

// AzericardStatusResponse is the status of one transaction type in a TRTYPE 90 reply.
func AzericardStatusResponse(data map[string]any, tranTrtype string) Status {
	if azApproved(data) {
		if tranTrtype == "0" {
			return StatusAuthorized
		}
		return StatusPaid
	}
	if data["responseCode"] == "-40" {
		return StatusPending // form in progress
	}
	return StatusFailed
}

func (p *Azericard) CreatePayment(_ context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if !azOrder.MatchString(req.OrderID) {
		return CreatePaymentResult{}, invalid(fmt.Sprintf("azericard: orderId must be 6–32 digits (got %q); its last 6 digits must be unique per terminal per day", req.OrderID))
	}
	trtype := "1"
	if req.Intent == IntentAuthorize {
		trtype = "0"
	}
	backref := req.CallbackURL
	if backref == "" {
		backref = req.ReturnURL
	}
	var fields []Field
	put := func(name, value string) {
		if value != "" {
			fields = append(fields, Field{name, value})
		}
	}
	put("AMOUNT", req.Amount.DecimalString())
	put("CURRENCY", req.Amount.Currency)
	put("ORDER", req.OrderID)
	put("DESC", truncate(orDefault(req.Description, "Order "+req.OrderID), 50))
	put("MERCH_NAME", truncate(p.cfg.MerchantName, 50))
	put("MERCH_URL", p.cfg.MerchantURL)
	put("TERMINAL", p.cfg.Terminal)
	put("EMAIL", p.cfg.Email)
	put("TRTYPE", trtype)
	put("COUNTRY", p.cfg.Country)
	put("MERCH_GMT", p.cfg.MerchantGMT)
	put("TIMESTAMP", AzericardTimestamp(p.cfg.Clock()))
	put("NONCE", p.cfg.Nonce())
	put("BACKREF", backref)
	put("LANG", orDefault(req.Language, "az"))
	signature, err := p.sign(fields, "purchase")
	if err != nil {
		return CreatePaymentResult{}, err
	}
	fields = append(fields, Field{"P_SIGN", signature})
	raw := map[string]any{}
	for _, f := range fields {
		raw[f.Name] = f.Value
	}
	return CreatePaymentResult{PaymentID: req.OrderID, Status: StatusPending, Next: NextAction{Type: "form", URL: p.url, Fields: fields},
		Raw: map[string]any{"fields": raw}}, nil
}

func (p *Azericard) VerifyCallback(_ context.Context, cb IncomingCallback) (PaymentEvent, error) {
	if p.gatewayKey == nil {
		return PaymentEvent{}, invalid("azericard: gatewayPublicKey is required to verify callbacks; never accept them unverified")
	}
	fields := azReadFields(cb)
	raw := map[string]any{}
	for k, v := range fields {
		raw[k] = v
	}
	signature := fields["P_SIGN"]
	sigBytes, err := hex.DecodeString(signature)
	if signature == "" || !azHex.MatchString(signature) || err != nil {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "azericard: callback has no valid P_SIGN", Provider: az, Raw: raw}
	}
	if !verifySHA256(p.gatewayKey, AzericardMacSource(fields, p.macFields["callback"]), sigBytes) {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "azericard: callback P_SIGN does not match", Provider: az, Raw: raw}
	}
	if fields["TERMINAL"] != p.cfg.Terminal {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Provider: az, Raw: raw,
			Message: fmt.Sprintf("azericard: callback is for terminal %s, not %s", fields["TERMINAL"], p.cfg.Terminal)}
	}
	order := fields["ORDER"]
	if order == "" {
		return PaymentEvent{}, &Error{Code: InvalidSignature, Message: "azericard: callback has no ORDER", Provider: az, Raw: raw}
	}
	amount, err := azParseAmount(fields["AMOUNT"], fields["CURRENCY"])
	if err != nil {
		return PaymentEvent{}, err
	}
	// ACTION is unsigned; the signed APPROVAL code is what proves an approval.
	approved := fields["ACTION"] == "0" && fields["APPROVAL"] != ""
	event := PaymentEvent{PaymentID: order, OrderID: order, Status: StatusFailed, Amount: amount, Raw: raw}
	if approved {
		event.Status = azStatusForTrtype(fields["TRTYPE"])
		if fields["RRN"] != "" && fields["INT_REF"] != "" && amount != nil && fields["TIMESTAMP"] != "" {
			event.ProviderRef = azEncodeRef(fields["RRN"], fields["INT_REF"], *amount, fields["TIMESTAMP"], orDefault(fields["TRTYPE"], "1"))
		}
	}
	return event, nil
}

// GetStatus asks TRTYPE 90, which answers for one transaction type, so the purchase (or pre-auth), completion and
// reversals are each queried.
func (p *Azericard) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	if !azOrder.MatchString(paymentID) {
		return PaymentState{}, invalid(fmt.Sprintf("azericard: paymentId must be the numeric ORDER, got %q", paymentID))
	}
	trtype := "1"
	data, err := p.queryStatus(ctx, paymentID, trtype)
	if err != nil {
		return PaymentState{}, err
	}
	if data["responseCode"] == "-24" {
		trtype = "0"
		if data, err = p.queryStatus(ctx, paymentID, trtype); err != nil {
			return PaymentState{}, err
		}
	}
	status := AzericardStatusResponse(data, trtype)
	amount, err := azParseAmount(text(data["amount"]), text(data["currency"]))
	if err != nil {
		return PaymentState{}, err
	}
	raw := map[string]any{trtype: data}
	if (status != StatusPaid && status != StatusAuthorized) || amount == nil {
		return PaymentState{PaymentID: paymentID, Status: status, Amount: amount, Raw: raw}, nil
	}
	types := []string{"22", "24"}
	if trtype == "0" {
		types = []string{"21", "22", "24"}
	}
	approved := map[string]map[string]any{}
	for _, t := range types {
		related, err := p.queryStatus(ctx, paymentID, t)
		if err != nil {
			return PaymentState{}, err
		}
		raw[t] = related
		if azApproved(related) {
			approved[t] = related
		}
	}
	if status == StatusAuthorized && approved["21"] != nil {
		status = StatusPaid
	}
	refunded, err := azApprovedAmount(approved["22"], amount.Currency)
	if err != nil {
		return PaymentState{}, err
	}
	offline, err := azApprovedAmount(approved["24"], amount.Currency)
	if err != nil {
		return PaymentState{}, err
	}
	refunded += offline
	state := PaymentState{PaymentID: paymentID, Amount: amount, Raw: raw}
	if refunded > 0 {
		state.RefundedAmount = &Money{Amount: refunded, Currency: amount.Currency}
		switch {
		case status == StatusAuthorized:
			status = StatusCancelled
		case refunded >= amount.Amount:
			status = StatusRefunded
		default:
			status = StatusPartiallyRefunded
		}
	}
	state.Status = status
	paidAt := text(data["timestamp"])
	if data["tranDate"] != nil {
		paidAt = azBakuTimeToGMT(text(data["tranDate"]))
	}
	rrn, intRef := text(data["rrn"]), text(data["intRef"])
	if rrn != "" && intRef != "" && paidAt != "" {
		refType := trtype
		if approved["21"] != nil {
			refType = "21"
		}
		state.ProviderRef = azEncodeRef(rrn, intRef, *amount, paidAt, refType)
	}
	return state, nil
}

func (p *Azericard) Capture(ctx context.Context, action PaymentAction) (PaymentState, error) {
	ref, err := azRequireRef(action.ProviderRef)
	if err != nil {
		return PaymentState{}, err
	}
	amount := ref.amount
	if action.Amount != nil {
		amount = *action.Amount
	}
	if _, err := p.send(ctx, "completion", "21", action.PaymentID, ref, amount); err != nil {
		return PaymentState{}, err
	}
	return PaymentState{PaymentID: action.PaymentID, Status: StatusPaid, Amount: &amount,
		ProviderRef: azEncodeRef(ref.rrn, ref.intRef, amount, ref.timestamp, "21")}, nil
}

func (p *Azericard) Cancel(ctx context.Context, action PaymentAction) (PaymentState, error) {
	ref, err := azRequireRef(action.ProviderRef)
	if err != nil {
		return PaymentState{}, err
	}
	if _, err := p.send(ctx, "reversal", "22", action.PaymentID, ref, ref.amount); err != nil {
		return PaymentState{}, err
	}
	amount := ref.amount
	return PaymentState{PaymentID: action.PaymentID, Status: StatusCancelled, Amount: &amount}, nil
}

func (p *Azericard) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	ref, err := azRequireRef(req.ProviderRef)
	if err != nil {
		return RefundResult{}, err
	}
	amount := ref.amount
	if req.Amount != nil {
		amount = *req.Amount
	}
	if amount.Currency != ref.amount.Currency || amount.Amount > ref.amount.Amount {
		return RefundResult{}, invalid("azericard: refund amount exceeds the paid amount or uses another currency")
	}
	// Online reversal (22) works once, before settlement; otherwise it is offline (24).
	trtype := "24"
	if paidAt, ok := azParseTimestamp(ref.timestamp); ok && p.cfg.Clock().Sub(paidAt) < 24*time.Hour {
		online, err := p.queryStatus(ctx, req.PaymentID, "22")
		if err != nil {
			return RefundResult{}, err
		}
		if !azApproved(online) {
			trtype = "22"
		}
	}
	action, err := p.send(ctx, "reversal", trtype, req.PaymentID, ref, amount)
	if err != nil {
		return RefundResult{}, err
	}
	status := StatusRefunded
	if amount.Amount < ref.amount.Amount {
		status = StatusPartiallyRefunded
	}
	return RefundResult{PaymentID: req.PaymentID, Status: status, Raw: map[string]any{"trtype": trtype, "action": action}}, nil
}

func (p *Azericard) queryStatus(ctx context.Context, order, tranTrtype string) (map[string]any, error) {
	fields := []Field{
		{"TRAN_TRTYPE", tranTrtype},
		{"ORDER", order},
		{"TERMINAL", p.cfg.Terminal},
		{"TRTYPE", "90"},
		{"TIMESTAMP", AzericardTimestamp(p.cfg.Clock())},
		{"NONCE", p.cfg.Nonce()},
	}
	signature, err := p.sign(fields, "status")
	if err != nil {
		return nil, err
	}
	fields = append(fields, Field{"P_SIGN", signature})
	status, raw, err := p.http.json(ctx, "POST", p.url, nil, nil, toPairs(fields))
	if err != nil {
		return nil, err
	}
	data := asMap(raw)
	if status >= 400 || data == nil {
		return nil, providerError(az, fmt.Sprintf("azericard: status request failed (HTTP %d)", status), raw)
	}
	return data, nil
}

func (p *Azericard) send(ctx context.Context, message, trtype, order string, ref azRef, amount Money) (string, error) {
	fields := []Field{
		{"AMOUNT", amount.DecimalString()},
		{"CURRENCY", amount.Currency},
		{"ORDER", order},
		{"RRN", ref.rrn},
		{"INT_REF", ref.intRef},
		{"TERMINAL", p.cfg.Terminal},
		{"TRTYPE", trtype},
		{"TIMESTAMP", AzericardTimestamp(p.cfg.Clock())},
		{"NONCE", p.cfg.Nonce()},
	}
	signature, err := p.sign(fields, message)
	if err != nil {
		return "", err
	}
	fields = append(fields, Field{"P_SIGN", signature})
	status, body, err := p.http.send(ctx, "POST", p.url, nil, nil, toPairs(fields))
	if err != nil {
		return "", err
	}
	action := strings.TrimSpace(body)
	if status >= 400 {
		return "", providerError(az, fmt.Sprintf("azericard: HTTP %d", status), body)
	}
	if action != "0" {
		text, ok := azActionMessages[action]
		if !ok {
			text = fmt.Sprintf("Unexpected response %q", cut(action, 100))
		}
		return "", &Error{Code: ProviderError, Message: "azericard: " + text, Provider: az, ProviderCode: cut(action, 20), ProviderMessage: text, Raw: body}
	}
	return action, nil
}

func (p *Azericard) sign(fields []Field, message string) (string, error) {
	m := map[string]string{}
	for _, f := range fields {
		m[f.Name] = f.Value
	}
	signature, err := signSHA256(p.privateKey, AzericardMacSource(m, p.macFields[message]))
	if err != nil {
		return "", &Error{Code: InvalidRequest, Message: "azericard: could not sign with privateKey", Provider: az, Err: err}
	}
	return hex.EncodeToString(signature), nil
}

func azApproved(data map[string]any) bool {
	return data["actionCode"] == "0" && data["responseCode"] == "00"
}

func azApprovedAmount(data map[string]any, currency string) (int64, error) {
	if data == nil {
		return 0, nil
	}
	code := currency
	if data["currency"] != nil {
		code = text(data["currency"])
	}
	m, err := azParseAmount(text(data["amount"]), code)
	if err != nil || m == nil || m.Currency != currency {
		return 0, err
	}
	return m.Amount, nil
}

// azBakuTimeToGMT turns "2026.10.26 19:06:40" in Baku time (UTC+4, no DST) into "20261026150640".
func azBakuTimeToGMT(value string) string {
	m := azBakuTime.FindStringSubmatch(value)
	if m == nil {
		return ""
	}
	n := make([]int, 6)
	for i := range n {
		n[i], _ = strconv.Atoi(m[i+1])
	}
	t := time.Date(n[0], time.Month(n[1]), n[2], n[3]-4, n[4], n[5], 0, time.UTC)
	return AzericardTimestamp(t)
}

func azStatusForTrtype(trtype string) Status {
	switch trtype {
	case "0":
		return StatusAuthorized
	case "22", "24":
		return StatusRefunded
	}
	return StatusPaid
}

func azEncodeRef(rrn, intRef string, amount Money, timestamp, trtype string) string {
	return strings.Join([]string{"az1", rrn, intRef, amount.DecimalString(), amount.Currency, timestamp, trtype}, "|")
}

// azRef is what providerRef carries: the payment's RRN and INT_REF, amount and GMT timestamp.
type azRef struct {
	rrn, intRef, timestamp string
	amount                 Money
}

func azRequireRef(value string) (azRef, error) {
	parts := strings.Split(value, "|")
	if len(parts) != 7 || parts[0] != "az1" || parts[1] == "" || parts[2] == "" {
		return azRef{}, invalid("azericard: providerRef from the payment callback is required (store event.providerRef with the order)")
	}
	if !supportedCurrency(parts[4]) {
		return azRef{}, providerError(az, fmt.Sprintf("azericard: unsupported currency %q", parts[4]), nil)
	}
	amount, err := ParseMoney(parts[3], parts[4])
	if err != nil {
		return azRef{}, err
	}
	return azRef{rrn: parts[1], intRef: parts[2], amount: amount, timestamp: parts[5]}, nil
}

func azParseAmount(amount, currency string) (*Money, error) {
	if amount == "" || currency == "" {
		return nil, nil
	}
	alpha := currency
	if azThreeDigits.MatchString(currency) {
		alpha = currencyFromNumeric(currency)
		if alpha == "" {
			return nil, nil
		}
	}
	if !supportedCurrency(alpha) {
		return nil, providerError(az, fmt.Sprintf("azericard: unsupported currency %q", alpha), nil)
	}
	m, err := ParseMoney(amount, alpha)
	if err != nil {
		return nil, err
	}
	return &m, nil
}

func azParseTimestamp(value string) (time.Time, bool) {
	t, err := time.ParseInLocation("20060102150405", value, time.UTC)
	return t, err == nil
}

// azReadFields reads upper-case keys with trimmed values; query first, body after.
func azReadFields(cb IncomingCallback) map[string]string {
	out := map[string]string{}
	for k, v := range cb.Query {
		if len(v) > 0 {
			out[strings.ToUpper(k)] = strings.TrimSpace(v[0])
		}
	}
	if cb.Body != "" {
		for k, v := range formDecode(cb.Body) {
			out[strings.ToUpper(k)] = strings.TrimSpace(v)
		}
	} else {
		for k, v := range cb.Fields {
			if s := firstString(v); s != "" || isString(v) {
				out[strings.ToUpper(k)] = strings.TrimSpace(s)
			}
		}
	}
	return out
}

func isString(v any) bool {
	_, ok := v.(string)
	return ok
}

func toPairs(fields []Field) []pair {
	out := make([]pair, len(fields))
	for i, f := range fields {
		out[i] = pair{f.Name, f.Value}
	}
	return out
}

func cut(s string, max int) string {
	if len(s) > max {
		return s[:max]
	}
	return s
}
