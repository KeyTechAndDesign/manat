package manat

import (
	"context"
	"io"
	"net/http"
	"net/url"
)

// Status is what every adapter maps its provider's codes onto.
type Status string

const (
	StatusPending           Status = "pending"
	StatusAuthorized        Status = "authorized" // held by a pre-authorisation; capture or cancel it
	StatusPaid              Status = "paid"
	StatusFailed            Status = "failed"
	StatusCancelled         Status = "cancelled"
	StatusRefunded          Status = "refunded"
	StatusPartiallyRefunded Status = "partially_refunded"
)

// Capability is an operation an adapter can offer.
type Capability string

const (
	CapCreatePayment  Capability = "createPayment"
	CapVerifyCallback Capability = "verifyCallback"
	CapGetStatus      Capability = "getStatus"
	CapRefund         Capability = "refund"
	CapCapture        Capability = "capture"
	CapCancel         Capability = "cancel"
)

// Intent values for CreatePaymentRequest.
const (
	IntentPurchase  = "purchase"
	IntentAuthorize = "authorize" // hold the amount; finish with Capture or release with Cancel
)

// CreatePaymentRequest is the input for Client.CreatePayment. Language ("az", "en", "ru") defaults to "az".
type CreatePaymentRequest struct {
	OrderID       string
	Amount        Money
	ReturnURL     string
	CallbackURL   string
	Description   string
	Language      string
	Intent        string
	CustomerEmail string
	CustomerPhone string
	Metadata      map[string]string
}

// RefundRequest refunds the whole payment when Amount is nil.
type RefundRequest struct {
	PaymentID   string
	Amount      *Money
	ProviderRef string // from PaymentEvent.ProviderRef, for providers that need it (Azericard)
}

// PaymentAction is the input for Capture and Cancel.
type PaymentAction struct {
	PaymentID   string
	Amount      *Money // capture less than was held; nil for the whole amount
	ProviderRef string
}

// IncomingCallback is what the merchant's HTTP handler received. Body is the raw body; Fields can hold a body your
// framework already parsed (form fields, or a JSON object) instead.
type IncomingCallback struct {
	Method string
	Query  url.Values
	Body   string
	Fields map[string]any
	Header http.Header
}

// CallbackFromRequest reads the method, query, headers and body of r.
func CallbackFromRequest(r *http.Request) (IncomingCallback, error) {
	body, err := io.ReadAll(r.Body)
	if err != nil {
		return IncomingCallback{}, err
	}
	return IncomingCallback{Method: r.Method, Query: r.URL.Query(), Body: string(body), Header: r.Header}, nil
}

func (c IncomingCallback) query(name string) string {
	return c.Query.Get(name)
}

// bodyField is a field of an urlencoded body or of Fields (first value of a list), when it is a string.
func (c IncomingCallback) bodyField(name string) string {
	if c.Body != "" {
		return formDecode(c.Body)[name]
	}
	return firstString(c.Fields[name])
}

func (c IncomingCallback) bodyValue() any {
	if c.Body != "" {
		return c.Body
	}
	return c.Fields
}

func (c IncomingCallback) toMap() map[string]any {
	return map[string]any{"method": c.Method, "query": c.Query, "body": c.bodyValue(), "header": c.Header}
}

// NextAction says where to send the customer: "redirect" to URL, "form" (POST Fields to URL), or "none".
type NextAction struct {
	Type   string
	URL    string
	Fields []Field
}

// Field is one form field; the order is the signed order.
type Field struct {
	Name  string
	Value string
}

// Method is "POST" for a form, otherwise empty.
func (n NextAction) Method() string {
	if n.Type == "form" {
		return "POST"
	}
	return ""
}

func (n NextAction) ToMap() map[string]any {
	out := map[string]any{"type": n.Type}
	if n.Type != "none" {
		out["url"] = n.URL
	}
	if n.Type == "form" {
		fields := map[string]any{}
		for _, f := range n.Fields {
			fields[f.Name] = f.Value
		}
		out["method"] = "POST"
		out["fields"] = fields
	}
	return out
}

// CreatePaymentResult has the provider's payment id (store it with the order) and where to send the customer.
type CreatePaymentResult struct {
	PaymentID string
	Status    Status
	Next      NextAction
	Raw       any
}

func (r CreatePaymentResult) ToMap() map[string]any {
	return map[string]any{"paymentId": r.PaymentID, "status": string(r.Status), "next": r.Next.ToMap(), "raw": r.Raw}
}

// PaymentState is the provider's current view of a payment.
type PaymentState struct {
	PaymentID      string
	Status         Status
	Amount         *Money
	RefundedAmount *Money
	ProviderRef    string
	Raw            any
}

func (s PaymentState) ToMap() map[string]any {
	out := map[string]any{"paymentId": s.PaymentID, "status": string(s.Status), "raw": s.Raw}
	if s.Amount != nil {
		out["amount"] = s.Amount.ToMap()
	}
	if s.RefundedAmount != nil {
		out["refundedAmount"] = s.RefundedAmount.ToMap()
	}
	if s.ProviderRef != "" {
		out["providerRef"] = s.ProviderRef
	}
	return out
}

// PaymentEvent is an authenticated callback. ProviderRef (Azericard) must be stored and passed back to
// Refund, Capture and Cancel.
type PaymentEvent struct {
	PaymentID   string
	OrderID     string
	Status      Status
	Amount      *Money
	ProviderRef string
	Raw         any
}

func eventFromState(s PaymentState) PaymentEvent {
	return PaymentEvent{PaymentID: s.PaymentID, Status: s.Status, Amount: s.Amount, Raw: s.Raw}
}

func (e PaymentEvent) ToMap() map[string]any {
	out := map[string]any{"paymentId": e.PaymentID, "status": string(e.Status), "raw": e.Raw}
	if e.OrderID != "" {
		out["orderId"] = e.OrderID
	}
	if e.Amount != nil {
		out["amount"] = e.Amount.ToMap()
	}
	if e.ProviderRef != "" {
		out["providerRef"] = e.ProviderRef
	}
	return out
}

// RefundResult is what Refund returns.
type RefundResult struct {
	PaymentID string
	RefundID  string
	Status    Status
	Raw       any
}

func (r RefundResult) ToMap() map[string]any {
	out := map[string]any{"paymentId": r.PaymentID, "status": string(r.Status), "raw": r.Raw}
	if r.RefundID != "" {
		out["refundId"] = r.RefundID
	}
	return out
}

// Provider is what every adapter implements. Use it through Client, which validates input first.
type Provider interface {
	Name() string
	Capabilities() []Capability
	CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error)
	VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error)
	GetStatus(ctx context.Context, paymentID string) (PaymentState, error)
	Refund(ctx context.Context, req RefundRequest) (RefundResult, error)
	Capture(ctx context.Context, action PaymentAction) (PaymentState, error)
	Cancel(ctx context.Context, action PaymentAction) (PaymentState, error)
}

func unsupported(provider string, capability Capability) *Error {
	return &Error{Code: UnsupportedOperation, Message: provider + " does not support " + string(capability), Provider: provider}
}
