package com.keytd.manat;

/** Input for {@link Manat#capture} and {@link Manat#cancel}. */
public final class PaymentAction {
    private final String paymentId;
    private final Money amount;
    private final String providerRef;

    private PaymentAction(String paymentId, Money amount, String providerRef) {
        this.paymentId = paymentId;
        this.amount = amount;
        this.providerRef = providerRef;
    }

    public static PaymentAction of(String paymentId) {
        return new PaymentAction(paymentId, null, null);
    }

    /** Capture less than was held. Without it the whole hold is captured. */
    public PaymentAction withAmount(Money amount) {
        return new PaymentAction(paymentId, amount, providerRef);
    }

    /** From {@link PaymentEvent#providerRef()}, for providers that need it. */
    public PaymentAction withProviderRef(String providerRef) {
        return new PaymentAction(paymentId, amount, providerRef);
    }

    public String paymentId() {
        return paymentId;
    }

    /** Or null for the whole amount. */
    public Money amount() {
        return amount;
    }

    /** Or null. */
    public String providerRef() {
        return providerRef;
    }
}
