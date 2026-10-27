package manat

import "errors"

// ErrorCode is the kind of failure.
type ErrorCode string

const (
	// InvalidRequest: the input is wrong; fix the call.
	InvalidRequest ErrorCode = "invalid_request"
	// InvalidSignature: a callback is not authentic. Never fulfil an order on it.
	InvalidSignature ErrorCode = "invalid_signature"
	// ProviderError: the provider refused the request or answered something unexpected.
	ProviderError ErrorCode = "provider_error"
	// NetworkError: the provider could not be reached, timed out or did not answer JSON.
	NetworkError ErrorCode = "network_error"
	// UnsupportedOperation: the provider has no such operation.
	UnsupportedOperation ErrorCode = "unsupported_operation"
)

// Error is every error the library returns. For provider errors ProviderCode and ProviderMessage hold what the
// provider said and Raw its whole reply.
type Error struct {
	Code            ErrorCode
	Message         string
	Provider        string
	ProviderCode    string
	ProviderMessage string
	Raw             any
	Err             error
}

func (e *Error) Error() string { return e.Message }

func (e *Error) Unwrap() error { return e.Err }

// IsError reports whether err is a manat *Error with this code.
func IsError(err error, code ErrorCode) bool {
	var e *Error
	return errors.As(err, &e) && e.Code == code
}

func invalid(message string) *Error {
	return &Error{Code: InvalidRequest, Message: message}
}

func providerError(provider, message string, raw any) *Error {
	return &Error{Code: ProviderError, Message: message, Provider: provider, Raw: raw}
}
