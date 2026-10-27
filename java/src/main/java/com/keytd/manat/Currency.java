package com.keytd.manat;

/** Currencies the adapters understand. Each has two minor-unit digits. */
public enum Currency {
    AZN("944"),
    USD("840"),
    EUR("978"),
    RUB("643"),
    TRY("949"),
    GBP("826");

    private final String numericCode;

    Currency(String numericCode) {
        this.numericCode = numericCode;
    }

    /** ISO 4217 numeric code ("944" for AZN), which several bank APIs use instead of letters. */
    public String numericCode() {
        return numericCode;
    }

    /** The currency with this letter code ("AZN"), or null. */
    public static Currency find(String code) {
        for (Currency currency : values()) {
            if (currency.name().equals(code)) {
                return currency;
            }
        }
        return null;
    }

    /** The currency with this ISO 4217 numeric code ("944"), or null. */
    public static Currency fromNumeric(String code) {
        for (Currency currency : values()) {
            if (currency.numericCode.equals(code)) {
                return currency;
            }
        }
        return null;
    }
}
