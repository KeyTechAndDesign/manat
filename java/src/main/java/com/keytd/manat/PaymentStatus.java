package com.keytd.manat;

/** The statuses every adapter maps its provider's codes onto. */
public enum PaymentStatus {
    PENDING("pending"),
    /** Funds are held (a pre-authorisation) and wait for capture or cancel. */
    AUTHORIZED("authorized"),
    PAID("paid"),
    FAILED("failed"),
    CANCELLED("cancelled"),
    REFUNDED("refunded"),
    PARTIALLY_REFUNDED("partially_refunded");

    private final String value;

    PaymentStatus(String value) {
        this.value = value;
    }

    /** The spec's name, such as {@code "partially_refunded"}. */
    public String value() {
        return value;
    }
}
