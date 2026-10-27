package com.keytd.manat;

/**
 * Every error the library throws. {@link #code()} says what kind of failure it is; for provider errors
 * {@link #providerCode()} and {@link #providerMessage()} hold what the provider said and {@link #raw()} its reply.
 */
public final class ManatError extends RuntimeException {
    private static final long serialVersionUID = 1L;

    /** The spec's error codes. */
    public enum Code {
        /** The input is wrong: fix the call. */
        INVALID_REQUEST("invalid_request"),
        /** A callback is not authentic. Never fulfil an order on it. */
        INVALID_SIGNATURE("invalid_signature"),
        /** The provider refused the request or answered something unexpected. */
        PROVIDER_ERROR("provider_error"),
        /** The provider could not be reached, timed out or did not answer JSON. */
        NETWORK_ERROR("network_error"),
        /** The provider has no such operation. */
        UNSUPPORTED_OPERATION("unsupported_operation");

        private final String value;

        Code(String value) {
            this.value = value;
        }

        /** The spec's name, such as {@code "provider_error"}. */
        public String value() {
            return value;
        }
    }

    private final Code code;
    private final String provider;
    private final String providerCode;
    private final String providerMessage;
    private final transient Object raw;

    public ManatError(Code code, String message) {
        this(code, message, null, null, null, null, null);
    }

    public ManatError(Code code, String message, String provider, String providerCode, String providerMessage, Object raw, Throwable cause) {
        super(message, cause);
        this.code = code;
        this.provider = provider;
        this.providerCode = providerCode;
        this.providerMessage = providerMessage;
        this.raw = raw;
    }

    public Code code() {
        return code;
    }

    /** The adapter's name, such as {@code "payriff"}, or null for input checks made before any adapter runs. */
    public String provider() {
        return provider;
    }

    /** The provider's own error code, when it gave one. */
    public String providerCode() {
        return providerCode;
    }

    /** The provider's own error text, when it gave one. */
    public String providerMessage() {
        return providerMessage;
    }

    /** The provider's whole reply (parsed JSON or text), when there was one. */
    public Object raw() {
        return raw;
    }

    /** True when {@code error} is a ManatError with this code. */
    public static boolean is(Throwable error, Code code) {
        return error instanceof ManatError && ((ManatError) error).code == code;
    }
}
