package com.keytd.manat;

/** The operations an adapter can offer; {@link Manat#supports(Capability)} tells which ones it does. */
public enum Capability {
    CREATE_PAYMENT("createPayment"),
    VERIFY_CALLBACK("verifyCallback"),
    GET_STATUS("getStatus"),
    REFUND("refund"),
    CAPTURE("capture"),
    CANCEL("cancel");

    private final String value;

    Capability(String value) {
        this.value = value;
    }

    /** The spec's name, such as {@code "getStatus"}. */
    public String value() {
        return value;
    }
}
