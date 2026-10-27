package com.keytd.manat;

import java.math.BigDecimal;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** An amount in minor units: 2550 AZN is 25.50 manat. There is no floating point anywhere. */
public final class Money {
    private static final Pattern DECIMAL = Pattern.compile("^(\\d+)(?:\\.(\\d+))?$");
    private static final int DIGITS = 2;

    private final long amount;
    private final Currency currency;

    private Money(long amount, Currency currency) {
        if (currency == null) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "currency is required");
        }
        if (amount < 0) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "amount must not be negative, got " + amount);
        }
        this.amount = amount;
        this.currency = currency;
    }

    /** {@code Money.of(2550, Currency.AZN)} is 25.50 AZN. */
    public static Money of(long minorUnits, Currency currency) {
        return new Money(minorUnits, currency);
    }

    /** {@code Money.azn("25.50")} is 2550 AZN. */
    public static Money azn(String value) {
        return fromDecimal(value, Currency.AZN);
    }

    /** "25.5" / "25.50" / "25" → 2550. Rejects more decimals than the currency has. */
    public static Money fromDecimal(String value, Currency currency) {
        String text = value == null ? "" : value.trim();
        Matcher match = DECIMAL.matcher(text);
        if (!match.matches() || (match.group(2) != null && match.group(2).length() > DIGITS)) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "\"" + text + "\" is not a valid " + currency + " amount");
        }
        StringBuilder fraction = new StringBuilder(match.group(2) == null ? "" : match.group(2));
        while (fraction.length() < DIGITS) {
            fraction.append('0');
        }
        try {
            return new Money(Long.parseLong(match.group(1) + fraction), currency);
        } catch (NumberFormatException tooLarge) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "\"" + text + "\" is too large", null, null, null, null, tooLarge);
        }
    }

    /** 25.5 → 2550, as a JSON number would read (trailing zeros do not count as decimals). */
    public static Money fromDecimal(BigDecimal value, Currency currency) {
        if (value == null) {
            throw new ManatError(ManatError.Code.INVALID_REQUEST, "amount is required");
        }
        BigDecimal plain = value.stripTrailingZeros();
        return fromDecimal(plain.scale() < 0 ? plain.setScale(0).toPlainString() : plain.toPlainString(), currency);
    }

    /** Minor units: 2550 for 25.50. */
    public long amount() {
        return amount;
    }

    public Currency currency() {
        return currency;
    }

    /** 2550 → "25.50". */
    public String toDecimalString() {
        StringBuilder units = new StringBuilder(Long.toString(amount));
        while (units.length() < DIGITS + 1) {
            units.insert(0, '0');
        }
        int split = units.length() - DIGITS;
        return units.substring(0, split) + "." + units.substring(split);
    }

    /** 2550 → 25.5 and 100 → 1, for JSON APIs that take amounts as numbers. */
    public BigDecimal toNumber() {
        return BigDecimal.valueOf(amount, DIGITS).stripTrailingZeros();
    }

    /** {@code {amount: 2550, currency: "AZN"}}, the spec's shape. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("amount", amount);
        out.put("currency", currency.name());
        return out;
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof Money && ((Money) other).amount == amount && ((Money) other).currency == currency;
    }

    @Override
    public int hashCode() {
        return Objects.hash(amount, currency);
    }

    /** "25.50 AZN". */
    @Override
    public String toString() {
        return toDecimalString() + " " + currency;
    }
}
