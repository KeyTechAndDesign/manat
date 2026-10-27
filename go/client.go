package manat

import (
	"context"
	"net/url"
	"strings"
)

// Client is the provider-neutral facade. It validates input once, then delegates to the adapter, so every provider
// gets the same checks and the same errors.
//
//	kb, _ := manat.NewKapitalBank(manat.KapitalBankConfig{Username: user, Password: password})
//	pay := manat.New(kb)
//	amount, _ := manat.AZN("25.50")
//	res, err := pay.CreatePayment(ctx, manat.CreatePaymentRequest{OrderID: "A-1001", Amount: amount, ReturnURL: returnURL})
//	http.Redirect(w, r, res.Next.URL, http.StatusFound)
type Client struct {
	Provider Provider
}

// New wraps an adapter.
func New(provider Provider) *Client { return &Client{Provider: provider} }

// Supports reports whether the adapter offers this operation.
func (c *Client) Supports(capability Capability) bool {
	for _, have := range c.Provider.Capabilities() {
		if have == capability {
			return true
		}
	}
	return false
}

// CreatePayment starts a payment; the result's Next says where to send the customer.
func (c *Client) CreatePayment(ctx context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	if req.OrderID == "" {
		return CreatePaymentResult{}, invalid("orderId is required")
	}
	if err := req.Amount.valid("amount"); err != nil {
		return CreatePaymentResult{}, err
	}
	if err := requireURL(req.ReturnURL, "returnUrl"); err != nil {
		return CreatePaymentResult{}, err
	}
	if req.CallbackURL != "" {
		if err := requireURL(req.CallbackURL, "callbackUrl"); err != nil {
			return CreatePaymentResult{}, err
		}
	}
	return c.Provider.CreatePayment(ctx, req)
}

// VerifyCallback authenticates a callback or redirect. An unsigned redirect is never trusted on its own: the
// adapter re-reads the payment from the provider.
func (c *Client) VerifyCallback(ctx context.Context, cb IncomingCallback) (PaymentEvent, error) {
	if cb.Method == "" {
		return PaymentEvent{}, invalid("callback.method is required")
	}
	return c.Provider.VerifyCallback(ctx, cb)
}

// GetStatus reads a payment's current state.
func (c *Client) GetStatus(ctx context.Context, paymentID string) (PaymentState, error) {
	if paymentID == "" {
		return PaymentState{}, invalid("paymentId is required")
	}
	if !c.Supports(CapGetStatus) {
		return PaymentState{}, unsupported(c.Provider.Name(), CapGetStatus)
	}
	return c.Provider.GetStatus(ctx, paymentID)
}

// Refund refunds the whole payment, or Amount of it.
func (c *Client) Refund(ctx context.Context, req RefundRequest) (RefundResult, error) {
	if req.PaymentID == "" {
		return RefundResult{}, invalid("paymentId is required")
	}
	if req.Amount != nil {
		if err := req.Amount.valid("amount"); err != nil {
			return RefundResult{}, err
		}
	}
	if !c.Supports(CapRefund) {
		return RefundResult{}, unsupported(c.Provider.Name(), CapRefund)
	}
	return c.Provider.Refund(ctx, req)
}

// Capture completes a payment made with IntentAuthorize.
func (c *Client) Capture(ctx context.Context, action PaymentAction) (PaymentState, error) {
	if action.PaymentID == "" {
		return PaymentState{}, invalid("paymentId is required")
	}
	if action.Amount != nil {
		if err := action.Amount.valid("amount"); err != nil {
			return PaymentState{}, err
		}
	}
	if !c.Supports(CapCapture) {
		return PaymentState{}, unsupported(c.Provider.Name(), CapCapture)
	}
	return c.Provider.Capture(ctx, action)
}

// Cancel releases a hold, or reverses a same-day payment where the provider allows it.
func (c *Client) Cancel(ctx context.Context, action PaymentAction) (PaymentState, error) {
	if action.PaymentID == "" {
		return PaymentState{}, invalid("paymentId is required")
	}
	if !c.Supports(CapCancel) {
		return PaymentState{}, unsupported(c.Provider.Name(), CapCancel)
	}
	return c.Provider.Cancel(ctx, action)
}

func requireURL(value, field string) error {
	u, err := url.Parse(value)
	if err == nil && (strings.EqualFold(u.Scheme, "http") || strings.EqualFold(u.Scheme, "https")) && u.Host != "" {
		return nil
	}
	return invalid(field + " must be an absolute http(s) URL")
}
