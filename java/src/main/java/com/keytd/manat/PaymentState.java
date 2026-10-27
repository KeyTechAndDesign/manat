package com.keytd.manat;

import java.util.LinkedHashMap;
import java.util.Map;

/** The provider's current view of a payment (getStatus, capture, cancel). */
public final class PaymentState {
    private final String paymentId;
    private final PaymentStatus status;
    private final Money amount;
    private final Money refundedAmount;
    private final String providerRef;
    private final Object raw;

    public PaymentState(String paymentId, PaymentStatus status, Money amount, Money refundedAmount, String providerRef, Object raw) {
        this.paymentId = paymentId;
        this.status = status;
        this.amount = amount;
        this.refundedAmount = refundedAmount;
        this.providerRef = providerRef;
        this.raw = raw;
    }

    public String paymentId() {
        return paymentId;
    }

    public PaymentStatus status() {
        return status;
    }

    /** Or null when the provider does not report it. */
    public Money amount() {
        return amount;
    }

    /** Or null when nothing was refunded or the provider does not report it. */
    public Money refundedAmount() {
        return refundedAmount;
    }

    /** Opaque data some providers need for later operations (Azericard); or null. */
    public String providerRef() {
        return providerRef;
    }

    /** The provider's response, untouched. */
    public Object raw() {
        return raw;
    }

    /** The spec's shape, for logging or JSON. Absent values are left out. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("paymentId", paymentId);
        out.put("status", status.value());
        if (amount != null) {
            out.put("amount", amount.toMap());
        }
        if (refundedAmount != null) {
            out.put("refundedAmount", refundedAmount.toMap());
        }
        if (providerRef != null) {
            out.put("providerRef", providerRef);
        }
        out.put("raw", raw);
        return out;
    }
}
