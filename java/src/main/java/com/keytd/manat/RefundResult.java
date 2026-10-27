package com.keytd.manat;

import java.util.LinkedHashMap;
import java.util.Map;

/** What refund returns. */
public final class RefundResult {
    private final String paymentId;
    private final String refundId;
    private final PaymentStatus status;
    private final Object raw;

    public RefundResult(String paymentId, String refundId, PaymentStatus status, Object raw) {
        this.paymentId = paymentId;
        this.refundId = refundId;
        this.status = status;
        this.raw = raw;
    }

    public String paymentId() {
        return paymentId;
    }

    /** The provider's id for the refund, when it gives one; or null. */
    public String refundId() {
        return refundId;
    }

    /** {@link PaymentStatus#REFUNDED} or {@link PaymentStatus#PARTIALLY_REFUNDED} on success. */
    public PaymentStatus status() {
        return status;
    }

    /** The provider's responses, untouched. */
    public Object raw() {
        return raw;
    }

    /** The spec's shape, for logging or JSON. Absent values are left out. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("paymentId", paymentId);
        if (refundId != null) {
            out.put("refundId", refundId);
        }
        out.put("status", status.value());
        out.put("raw", raw);
        return out;
    }
}
