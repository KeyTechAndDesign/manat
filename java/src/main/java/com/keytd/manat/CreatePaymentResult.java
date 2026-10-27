package com.keytd.manat;

import java.util.LinkedHashMap;
import java.util.Map;

/** What createPayment returns: the provider's payment id and where to send the customer. */
public final class CreatePaymentResult {
    private final String paymentId;
    private final PaymentStatus status;
    private final NextAction next;
    private final Object raw;

    public CreatePaymentResult(String paymentId, PaymentStatus status, NextAction next, Object raw) {
        this.paymentId = paymentId;
        this.status = status;
        this.next = next;
        this.raw = raw;
    }

    /** The provider's id for this payment (or the orderId when the provider has none). Store it with the order. */
    public String paymentId() {
        return paymentId;
    }

    /** Usually {@link PaymentStatus#PENDING}. */
    public PaymentStatus status() {
        return status;
    }

    public NextAction next() {
        return next;
    }

    /** The provider's response, untouched (maps, lists, strings, numbers as parsed from JSON). */
    public Object raw() {
        return raw;
    }

    /** The spec's shape, for logging or JSON. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("paymentId", paymentId);
        out.put("status", status.value());
        out.put("next", next.toMap());
        out.put("raw", raw);
        return out;
    }
}
