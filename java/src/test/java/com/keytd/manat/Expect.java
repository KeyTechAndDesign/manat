package com.keytd.manat;

import java.math.BigDecimal;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

/**
 * Comparisons with the vectors' JSON semantics: numbers compare by value (1 equals 1.0),
 * a string never equals a number.
 */
final class Expect {
    private Expect() {
    }

    /** Deep equality, like JavaScript's toEqual. */
    static void equal(Object expected, Object actual, String path) {
        compare(expected, actual, path, false);
    }

    /** Every key in {@code expected} must be in {@code actual} with a matching value, like toMatchObject. */
    static void subset(Object expected, Object actual, String path) {
        compare(expected, actual, path, true);
    }

    private static void compare(Object expected, Object actual, String path, boolean subset) {
        if (expected instanceof Map) {
            if (!(actual instanceof Map)) {
                fail(path + ": expected an object, got " + actual);
            }
            Map<?, ?> want = (Map<?, ?>) expected;
            Map<?, ?> got = (Map<?, ?>) actual;
            if (!subset) {
                assertEquals(keys(want), keys(got), path + ": keys");
            }
            for (Map.Entry<?, ?> entry : want.entrySet()) {
                String key = String.valueOf(entry.getKey());
                assertTrue(got.containsKey(key), path + "." + key + " is missing");
                compare(entry.getValue(), got.get(key), path + "." + key, subset);
            }
            return;
        }
        if (expected instanceof List) {
            if (!(actual instanceof List)) {
                fail(path + ": expected a list, got " + actual);
            }
            List<?> want = (List<?>) expected;
            List<?> got = (List<?>) actual;
            assertEquals(want.size(), got.size(), path + ": list length");
            for (int i = 0; i < want.size(); i++) {
                compare(want.get(i), got.get(i), path + "[" + i + "]", subset);
            }
            return;
        }
        if (expected instanceof Number && actual instanceof Number) {
            assertEquals(0, decimal(expected).compareTo(decimal(actual)), path + ": expected " + expected + ", got " + actual);
            return;
        }
        assertEquals(expected, actual, path);
    }

    private static Set<String> keys(Map<?, ?> map) {
        Set<String> out = new HashSet<>();
        map.keySet().forEach(key -> out.add(String.valueOf(key)));
        return out;
    }

    private static BigDecimal decimal(Object number) {
        return number instanceof BigDecimal ? (BigDecimal) number : new BigDecimal(number.toString());
    }
}
