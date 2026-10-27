package com.keytd.manat.internal;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Minimal JSON reader and writer, so the library needs no dependency.
 * Objects become {@code LinkedHashMap<String, Object>}, arrays {@code ArrayList<Object>}, integers {@code Long}
 * (or {@code BigDecimal} when too large), other numbers {@code BigDecimal}.
 * Output matches JavaScript's JSON.stringify: compact, {@code /} and non-ASCII unescaped, so signed payloads
 * are byte-for-byte the same in every language.
 */
public final class Json {
    private static final int MAX_DEPTH = 512;

    private Json() {
    }

    /** Thrown for text that is not JSON. */
    public static final class ParseException extends IllegalArgumentException {
        private static final long serialVersionUID = 1L;

        ParseException(String message, int position) {
            super(message + " at position " + position);
        }
    }

    public static Object parse(String text) {
        Reader reader = new Reader(text);
        reader.skipWhitespace();
        Object value = reader.value(0);
        reader.skipWhitespace();
        if (!reader.atEnd()) {
            throw reader.error("unexpected text after JSON");
        }
        return value;
    }

    public static String stringify(Object value) {
        StringBuilder out = new StringBuilder();
        write(out, value);
        return out.toString();
    }

    private static void write(StringBuilder out, Object value) {
        if (value == null) {
            out.append("null");
        } else if (value instanceof String) {
            quote(out, (String) value);
        } else if (value instanceof Boolean) {
            out.append(value);
        } else if (value instanceof BigDecimal) {
            BigDecimal plain = ((BigDecimal) value).stripTrailingZeros();
            out.append(plain.signum() == 0 ? "0" : plain.toPlainString());
        } else if (value instanceof Double || value instanceof Float) {
            double number = ((Number) value).doubleValue();
            if (Double.isNaN(number) || Double.isInfinite(number)) {
                out.append("null");
            } else {
                write(out, BigDecimal.valueOf(number));
            }
        } else if (value instanceof Number) {
            out.append(value);
        } else if (value instanceof Map) {
            out.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> entry : ((Map<?, ?>) value).entrySet()) {
                if (!first) {
                    out.append(',');
                }
                first = false;
                quote(out, String.valueOf(entry.getKey()));
                out.append(':');
                write(out, entry.getValue());
            }
            out.append('}');
        } else if (value instanceof Iterable) {
            out.append('[');
            boolean first = true;
            for (Object item : (Iterable<?>) value) {
                if (!first) {
                    out.append(',');
                }
                first = false;
                write(out, item);
            }
            out.append(']');
        } else {
            throw new IllegalArgumentException("cannot write " + value.getClass().getName() + " as JSON");
        }
    }

    private static void quote(StringBuilder out, String text) {
        out.append('"');
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '"':
                    out.append("\\\"");
                    break;
                case '\\':
                    out.append("\\\\");
                    break;
                case '\b':
                    out.append("\\b");
                    break;
                case '\f':
                    out.append("\\f");
                    break;
                case '\n':
                    out.append("\\n");
                    break;
                case '\r':
                    out.append("\\r");
                    break;
                case '\t':
                    out.append("\\t");
                    break;
                default:
                    boolean loneSurrogate = Character.isHighSurrogate(c)
                            ? i + 1 >= text.length() || !Character.isLowSurrogate(text.charAt(i + 1))
                            : Character.isLowSurrogate(c) && (i == 0 || !Character.isHighSurrogate(text.charAt(i - 1)));
                    if (c < 0x20 || loneSurrogate) {
                        out.append(String.format("\\u%04x", (int) c));
                    } else {
                        out.append(c);
                    }
            }
        }
        out.append('"');
    }

    private static final class Reader {
        private final String text;
        private int pos;

        Reader(String text) {
            this.text = text == null ? "" : text;
        }

        boolean atEnd() {
            return pos >= text.length();
        }

        ParseException error(String message) {
            return new ParseException(message, pos);
        }

        void skipWhitespace() {
            while (pos < text.length()) {
                char c = text.charAt(pos);
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') {
                    return;
                }
                pos++;
            }
        }

        Object value(int depth) {
            if (depth > MAX_DEPTH) {
                throw error("JSON nested too deeply");
            }
            if (atEnd()) {
                throw error("unexpected end of JSON");
            }
            char c = text.charAt(pos);
            switch (c) {
                case '{':
                    return object(depth);
                case '[':
                    return array(depth);
                case '"':
                    return string();
                case 't':
                    return literal("true", Boolean.TRUE);
                case 'f':
                    return literal("false", Boolean.FALSE);
                case 'n':
                    return literal("null", null);
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) {
                        return number();
                    }
                    throw error("unexpected character '" + c + "'");
            }
        }

        private Object literal(String word, Object value) {
            if (!text.startsWith(word, pos)) {
                throw error("unexpected text");
            }
            pos += word.length();
            return value;
        }

        private Map<String, Object> object(int depth) {
            Map<String, Object> out = new LinkedHashMap<>();
            pos++;
            skipWhitespace();
            if (!atEnd() && text.charAt(pos) == '}') {
                pos++;
                return out;
            }
            while (true) {
                skipWhitespace();
                if (atEnd() || text.charAt(pos) != '"') {
                    throw error("expected a property name");
                }
                String key = string();
                skipWhitespace();
                expect(':');
                skipWhitespace();
                out.put(key, value(depth + 1));
                skipWhitespace();
                if (atEnd()) {
                    throw error("unexpected end of JSON");
                }
                char c = text.charAt(pos++);
                if (c == '}') {
                    return out;
                }
                if (c != ',') {
                    throw error("expected ',' or '}'");
                }
            }
        }

        private List<Object> array(int depth) {
            List<Object> out = new ArrayList<>();
            pos++;
            skipWhitespace();
            if (!atEnd() && text.charAt(pos) == ']') {
                pos++;
                return out;
            }
            while (true) {
                skipWhitespace();
                out.add(value(depth + 1));
                skipWhitespace();
                if (atEnd()) {
                    throw error("unexpected end of JSON");
                }
                char c = text.charAt(pos++);
                if (c == ']') {
                    return out;
                }
                if (c != ',') {
                    throw error("expected ',' or ']'");
                }
            }
        }

        private void expect(char c) {
            if (atEnd() || text.charAt(pos) != c) {
                throw error("expected '" + c + "'");
            }
            pos++;
        }

        private String string() {
            pos++;
            StringBuilder out = new StringBuilder();
            while (true) {
                if (atEnd()) {
                    throw error("unterminated string");
                }
                char c = text.charAt(pos++);
                if (c == '"') {
                    return out.toString();
                }
                if (c < 0x20) {
                    throw error("control character in string");
                }
                if (c != '\\') {
                    out.append(c);
                    continue;
                }
                if (atEnd()) {
                    throw error("unterminated escape");
                }
                char e = text.charAt(pos++);
                switch (e) {
                    case '"':
                    case '\\':
                    case '/':
                        out.append(e);
                        break;
                    case 'b':
                        out.append('\b');
                        break;
                    case 'f':
                        out.append('\f');
                        break;
                    case 'n':
                        out.append('\n');
                        break;
                    case 'r':
                        out.append('\r');
                        break;
                    case 't':
                        out.append('\t');
                        break;
                    case 'u':
                        if (pos + 4 > text.length()) {
                            throw error("bad unicode escape");
                        }
                        try {
                            out.append((char) Integer.parseInt(text.substring(pos, pos + 4), 16));
                        } catch (NumberFormatException bad) {
                            throw error("bad unicode escape");
                        }
                        pos += 4;
                        break;
                    default:
                        throw error("bad escape '\\" + e + "'");
                }
            }
        }

        private Object number() {
            int start = pos;
            if (text.charAt(pos) == '-') {
                pos++;
            }
            if (atEnd()) {
                throw error("bad number");
            }
            if (text.charAt(pos) == '0') {
                pos++;
            } else if (!digits()) {
                throw error("bad number");
            }
            boolean integer = true;
            if (!atEnd() && text.charAt(pos) == '.') {
                integer = false;
                pos++;
                if (!digits()) {
                    throw error("bad number");
                }
            }
            if (!atEnd() && (text.charAt(pos) == 'e' || text.charAt(pos) == 'E')) {
                integer = false;
                pos++;
                if (!atEnd() && (text.charAt(pos) == '+' || text.charAt(pos) == '-')) {
                    pos++;
                }
                if (!digits()) {
                    throw error("bad number");
                }
            }
            String literal = text.substring(start, pos);
            if (integer) {
                BigInteger value = new BigInteger(literal);
                return value.bitLength() < 64 ? (Object) value.longValue() : new BigDecimal(value);
            }
            return new BigDecimal(literal);
        }

        private boolean digits() {
            int start = pos;
            while (!atEnd() && text.charAt(pos) >= '0' && text.charAt(pos) <= '9') {
                pos++;
            }
            return pos > start;
        }
    }
}
