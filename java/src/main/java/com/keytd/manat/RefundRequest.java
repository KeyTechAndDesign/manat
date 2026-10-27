package com.keytd.manat;

/**
 * Input for {@link Manat#refund}. Without an amount the whole payment is refunded.
 *
 * <pre>{@code
 * RefundRequest.of(paymentId);                                   // full refund
 * RefundRequest.of(paymentId).withAmount(Money.azn("10.00"));    // partial
 * RefundRequest.of(paymentId).withProviderRef(event.providerRef()); // Azericard
 * }</pre>
 */
public final class RefundRequest {
    private final String paymentId;
    private final Money amount;
    private final String providerRef;

    private RefundRequest(String paymentId, Money amount, String providerRef) {
        this.paymentId = paymentId;
        this.amount = amount;
        this.providerRef = providerRef;
    }

    public static RefundRequest of(String paymentId) {
        return new RefundRequest(paymentId, null, null);
    }

    public RefundRequest withAmount(Money amount) {
        return new RefundRequest(paymentId, amount, providerRef);
    }

    /** From {@link PaymentEvent#providerRef()}, for providers that need it. */
    public RefundRequest withProviderRef(String providerRef) {
        return new RefundRequest(paymentId, amount, providerRef);
    }

    public String paymentId() {
        return paymentId;
    }

    /** Or null for a full refund. */
    public Money amount() {
        return amount;
    }

    /** Or null. */
    public String providerRef() {
        return providerRef;
    }
}
