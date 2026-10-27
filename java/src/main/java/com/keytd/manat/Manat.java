package com.keytd.manat;

import java.net.URI;
import java.net.URISyntaxException;
import java.util.Locale;
import java.util.Set;

/**
 * Provider-neutral facade. Validates input once, then delegates to the adapter,
 * so every provider gets the same checks and the same errors.
 *
 * <pre>{@code
 * Manat pay = new Manat(KapitalBank.builder().username(user).password(password).build());
 * CreatePaymentResult result = pay.createPayment(
 *     CreatePaymentRequest.of("A-1001", Money.azn("25.50"), "https://shop.az/payment/return"));
 * response.sendRedirect(result.next().url());
 * }</pre>
 */
public final class Manat {
    private final PaymentProvider provider;

    public Manat(PaymentProvider provider) {
        if (provider == null) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "provider is required");
        }
        this.provider = provider;
    }

    public PaymentProvider provider() {
        return provider;
    }

    public Set<Capability> capabilities() {
        return provider.capabilities();
    }

    public boolean supports(Capability capability) {
        return provider.capabilities().contains(capability);
    }

    /** Starts a payment; {@link CreatePaymentResult#next()} says where to send the customer. */
    public CreatePaymentResult createPayment(CreatePaymentRequest request) {
        if (request == null || request.orderId() == null || request.orderId().isEmpty()) {
            throw invalid("orderId is required");
        }
        requireMoney(request.amount());
        requireUrl(request.returnUrl(), "returnUrl");
        if (request.callbackUrl() != null) {
            requireUrl(request.callbackUrl(), "callbackUrl");
        }
        return provider.createPayment(request);
    }

    /**
     * Authenticates a provider callback or redirect. Throws {@code invalid_signature} when it is not authentic;
     * an unsigned redirect is never trusted on its own (the adapter re-reads the payment from the provider).
     */
    public PaymentEvent verifyCallback(IncomingCallback callback) {
        if (callback == null || callback.method() == null) {
            throw invalid("callback.method is required");
        }
        return provider.verifyCallback(callback);
    }

    public PaymentState getStatus(String paymentId) {
        requirePaymentId(paymentId);
        require(Capability.GET_STATUS);
        return provider.getStatus(paymentId);
    }

    /** Full refund without an amount, partial with one. */
    public RefundResult refund(RefundRequest request) {
        requirePaymentId(request == null ? null : request.paymentId());
        if (request.amount() != null) {
            requireMoney(request.amount());
        }
        require(Capability.REFUND);
        return provider.refund(request);
    }

    /** Completes a payment made with {@link Intent#AUTHORIZE}. */
    public PaymentState capture(PaymentAction action) {
        requirePaymentId(action == null ? null : action.paymentId());
        if (action.amount() != null) {
            requireMoney(action.amount());
        }
        require(Capability.CAPTURE);
        return provider.capture(action);
    }

    /** Releases a hold, or reverses a same-day payment where the provider allows it. */
    public PaymentState cancel(PaymentAction action) {
        requirePaymentId(action == null ? null : action.paymentId());
        require(Capability.CANCEL);
        return provider.cancel(action);
    }

    private void require(Capability capability) {
        if (!supports(capability)) {
            throw provider.unsupported(capability);
        }
    }

    private static void requirePaymentId(String paymentId) {
        if (paymentId == null || paymentId.isEmpty()) {
            throw invalid("paymentId is required");
        }
    }

    private static void requireMoney(Money money) {
        if (money == null) {
            throw invalid("amount is required");
        }
        if (money.amount() < 1) {
            throw invalid("amount.amount must be a positive integer in minor units (2550 = 25.50), got " + money.amount());
        }
    }

    private static void requireUrl(String value, String field) {
        try {
            URI uri = new URI(value == null ? "" : value);
            String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
            if ((scheme.equals("http") || scheme.equals("https")) && uri.getRawAuthority() != null && !uri.getRawAuthority().isEmpty()) {
                return;
            }
        } catch (URISyntaxException ignored) {
            // reported below
        }
        throw invalid(field + " must be an absolute http(s) URL");
    }

    private static ManatError invalid(String message) {
        return new ManatError(ManatError.Code.INVALID_REQUEST, message);
    }
}
