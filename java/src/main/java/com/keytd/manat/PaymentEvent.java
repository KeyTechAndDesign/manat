package com.keytd.manat;

import java.util.LinkedHashMap;
import java.util.Map;

/** An authenticated provider callback or redirect, from {@link Manat#verifyCallback}. */
public final class PaymentEvent {
    private final String paymentId;
    private final String orderId;
    private final PaymentStatus status;
    private final Money amount;
    private final String providerRef;
    private final Object raw;

    public PaymentEvent(String paymentId, String orderId, PaymentStatus status, Money amount, String providerRef, Object raw) {
        this.paymentId = paymentId;
        this.orderId = orderId;
        this.status = status;
        this.amount = amount;
        this.providerRef = providerRef;
        this.raw = raw;
    }

    /** The event for a payment re-read from the provider's API, which is how unsigned redirects are handled. */
    public static PaymentEvent fromState(PaymentState state) {
        return new PaymentEvent(state.paymentId(), null, state.status(), state.amount(), null, state.raw());
    }

    public String paymentId() {
        return paymentId;
    }

    /** The merchant's orderId when the provider echoes it; or null. */
    public String orderId() {
        return orderId;
    }

    public PaymentStatus status() {
        return status;
    }

    /** Or null when the provider does not report it. */
    public Money amount() {
        return amount;
    }

    /**
     * Opaque provider data needed for later operations (Azericard's RRN and INT_REF), or null.
     * Store it with the order and pass it back to refund/capture/cancel unchanged.
     */
    public String providerRef() {
        return providerRef;
    }

    /** What the provider sent or answered, untouched. */
    public Object raw() {
        return raw;
    }

    /** The spec's shape, for logging or JSON. Absent values are left out. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("paymentId", paymentId);
        if (orderId != null) {
            out.put("orderId", orderId);
        }
        out.put("status", status.value());
        if (amount != null) {
            out.put("amount", amount.toMap());
        }
        if (providerRef != null) {
            out.put("providerRef", providerRef);
        }
        out.put("raw", raw);
        return out;
    }
}
