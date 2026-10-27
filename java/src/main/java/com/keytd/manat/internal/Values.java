package com.keytd.manat.internal;

import com.keytd.manat.Currency;
import com.keytd.manat.Money;

import java.math.BigDecimal;
import java.util.List;
import java.util.Map;

/** Reading parsed JSON (maps, lists, strings, Long, BigDecimal) without casts all over the adapters. */
public final class Values {
    private Values() {
    }

    /** {@code value} as an object, or null when it is something else. */
    @SuppressWarnings("unchecked")
    public static Map<String, Object> map(Object value) {
        return value instanceof Map ? (Map<String, Object>) value : null;
    }

    /** {@code value} as a list, or null when it is something else. */
    @SuppressWarnings("unchecked")
    public static List<Object> list(Object value) {
        return value instanceof List ? (List<Object>) value : null;
    }

    /** Walks object keys: {@code get(data, "order", "id")}. Null when any step is missing. */
    public static Object get(Object value, String... path) {
        Object current = value;
        for (String key : path) {
            Map<String, Object> map = map(current);
            if (map == null) {
                return null;
            }
            current = map.get(key);
        }
        return current;
    }

    /** The value when it is a string, otherwise null. */
    public static String string(Object value) {
        return value instanceof String ? (String) value : null;
    }

    /** JavaScript's {@code String(value)} for strings and numbers; null for anything else. */
    public static String text(Object value) {
        if (value instanceof String) {
            return (String) value;
        }
        if (value instanceof BigDecimal) {
            return ((BigDecimal) value).stripTrailingZeros().toPlainString();
        }
        if (value instanceof Number || value instanceof Boolean) {
            return value.toString();
        }
        return null;
    }

    /** JavaScript truthiness: not null, false, "", or 0. */
    public static boolean truthy(Object value) {
        if (value == null || Boolean.FALSE.equals(value) || "".equals(value)) {
            return false;
        }
        if (value instanceof BigDecimal) {
            return ((BigDecimal) value).signum() != 0;
        }
        if (value instanceof Number) {
            return ((Number) value).doubleValue() != 0;
        }
        return true;
    }

    /** A JSON integer as a Long, or null for anything else (strings, decimals with a fraction). */
    public static Long integer(Object value) {
        if (value instanceof Long || value instanceof Integer) {
            return ((Number) value).longValue();
        }
        if (value instanceof BigDecimal) {
            try {
                return ((BigDecimal) value).longValueExact();
            } catch (ArithmeticException notInteger) {
                return null;
            }
        }
        return null;
    }

    /** JavaScript's {@code Number(value)} for numbers and numeric strings, as a decimal; null when not a number. */
    public static BigDecimal decimal(Object value) {
        if (value instanceof BigDecimal) {
            return (BigDecimal) value;
        }
        if (value instanceof Long || value instanceof Integer) {
            return BigDecimal.valueOf(((Number) value).longValue());
        }
        if (value instanceof String) {
            String text = ((String) value).trim();
            if (text.isEmpty()) {
                return BigDecimal.ZERO;
            }
            try {
                return new BigDecimal(text);
            } catch (NumberFormatException notNumeric) {
                return null;
            }
        }
        return null;
    }

    /** A decimal amount from JSON (number or string, "25.5" or 25.5) as Money. */
    public static Money money(Object value, Currency currency) {
        if (value instanceof BigDecimal) {
            return Money.fromDecimal((BigDecimal) value, currency);
        }
        if (value instanceof Long || value instanceof Integer) {
            return Money.fromDecimal(BigDecimal.valueOf(((Number) value).longValue()), currency);
        }
        return Money.fromDecimal(String.valueOf(value), currency);
    }

    /** Code points, so a cut never splits a character. */
    public static String truncate(String value, int maxCodePoints) {
        if (value.codePointCount(0, value.length()) <= maxCodePoints) {
            return value;
        }
        return value.substring(0, value.offsetByCodePoints(0, maxCodePoints));
    }
}
