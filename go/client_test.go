package manat

import (
	"context"
	"os"
	"regexp"
	"testing"
)

// fakeProvider records calls and offers createPayment, verifyCallback and getStatus.
type fakeProvider struct{ calls []string }

func (f *fakeProvider) Name() string { return "fake" }

func (f *fakeProvider) Capabilities() []Capability {
	return []Capability{CapCreatePayment, CapVerifyCallback, CapGetStatus}
}

func (f *fakeProvider) CreatePayment(_ context.Context, req CreatePaymentRequest) (CreatePaymentResult, error) {
	f.calls = append(f.calls, "createPayment")
	return CreatePaymentResult{PaymentID: "p-" + req.OrderID, Status: StatusPending, Next: NextAction{Type: "redirect", URL: "https://pay.example/1"}}, nil
}

func (f *fakeProvider) VerifyCallback(context.Context, IncomingCallback) (PaymentEvent, error) {
	f.calls = append(f.calls, "verifyCallback")
	return PaymentEvent{PaymentID: "p-1", Status: StatusPaid}, nil
}

func (f *fakeProvider) GetStatus(_ context.Context, id string) (PaymentState, error) {
	f.calls = append(f.calls, "getStatus")
	return PaymentState{PaymentID: id, Status: StatusPaid}, nil
}

func (f *fakeProvider) Refund(context.Context, RefundRequest) (RefundResult, error) {
	return RefundResult{}, unsupported("fake", CapRefund)
}

func (f *fakeProvider) Capture(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported("fake", CapCapture)
}

func (f *fakeProvider) Cancel(context.Context, PaymentAction) (PaymentState, error) {
	return PaymentState{}, unsupported("fake", CapCancel)
}

var valid = CreatePaymentRequest{OrderID: "A-1", Amount: Money{Amount: 2550, Currency: "AZN"}, ReturnURL: "https://shop.example/return"}

func TestClientDelegatesValidRequests(t *testing.T) {
	f := &fakeProvider{}
	res, err := New(f).CreatePayment(context.Background(), valid)
	if err != nil || res.PaymentID != "p-A-1" || len(f.calls) != 1 {
		t.Fatalf("got %v %v %v", res, err, f.calls)
	}
}

func TestClientValidatesBeforeCallingTheProvider(t *testing.T) {
	f := &fakeProvider{}
	c := New(f)
	ctx := context.Background()
	bad := []CreatePaymentRequest{
		{Amount: valid.Amount, ReturnURL: valid.ReturnURL},
		{OrderID: "A-1", ReturnURL: valid.ReturnURL},
		{OrderID: "A-1", Amount: Money{Amount: 0, Currency: "AZN"}, ReturnURL: valid.ReturnURL},
		{OrderID: "A-1", Amount: Money{Amount: 100, Currency: "XYZ"}, ReturnURL: valid.ReturnURL},
		{OrderID: "A-1", Amount: valid.Amount, ReturnURL: "javascript:alert(1)"},
		{OrderID: "A-1", Amount: valid.Amount, ReturnURL: valid.ReturnURL, CallbackURL: "/relative"},
	}
	for i, req := range bad {
		if _, err := c.CreatePayment(ctx, req); !IsError(err, InvalidRequest) {
			t.Errorf("case %d: got %v, want invalid_request", i, err)
		}
	}
	if _, err := c.VerifyCallback(ctx, IncomingCallback{}); !IsError(err, InvalidRequest) {
		t.Errorf("callback without method: %v", err)
	}
	if len(f.calls) != 0 {
		t.Errorf("provider was called: %v", f.calls)
	}
}

func TestClientReportsUnsupportedOperations(t *testing.T) {
	c := New(&fakeProvider{})
	if c.Supports(CapRefund) {
		t.Error("fake does not refund")
	}
	if _, err := c.Refund(context.Background(), RefundRequest{PaymentID: "p-1"}); !IsError(err, UnsupportedOperation) {
		t.Errorf("refund: %v", err)
	}
	if _, err := c.Capture(context.Background(), PaymentAction{PaymentID: "p-1"}); !IsError(err, UnsupportedOperation) {
		t.Errorf("capture: %v", err)
	}
}

func TestClientRequiresAPaymentID(t *testing.T) {
	c := New(&fakeProvider{})
	if _, err := c.GetStatus(context.Background(), ""); !IsError(err, InvalidRequest) {
		t.Errorf("getStatus: %v", err)
	}
	if _, err := c.Refund(context.Background(), RefundRequest{}); !IsError(err, InvalidRequest) {
		t.Errorf("refund: %v", err)
	}
}

// The package is advertised as dependency-free.
func TestNoDependencies(t *testing.T) {
	mod, err := os.ReadFile("go.mod")
	if err != nil {
		t.Fatal(err)
	}
	if regexp.MustCompile(`(?m)^\s*require\b`).Match(mod) {
		t.Errorf("go.mod has require directives:\n%s", mod)
	}
}
