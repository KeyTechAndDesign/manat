package com.keytd.manat.internal;

import java.io.ByteArrayOutputStream;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;

/** application/x-www-form-urlencoded, encoded like JavaScript's URLSearchParams. */
public final class Form {
    private Form() {
    }

    public static String encode(Map<String, String> fields) {
        StringBuilder out = new StringBuilder();
        for (Map.Entry<String, String> field : fields.entrySet()) {
            if (out.length() > 0) {
                out.append('&');
            }
            out.append(URLEncoder.encode(field.getKey(), StandardCharsets.UTF_8))
                    .append('=')
                    .append(URLEncoder.encode(field.getValue() == null ? "" : field.getValue(), StandardCharsets.UTF_8));
        }
        return out.toString();
    }

    /** Later duplicates are ignored; malformed %-escapes are kept as text. */
    public static Map<String, String> decode(String body) {
        Map<String, String> out = new LinkedHashMap<>();
        if (body == null || body.isEmpty()) {
            return out;
        }
        for (String pair : body.split("&", -1)) {
            if (pair.isEmpty()) {
                continue;
            }
            int eq = pair.indexOf('=');
            String key = unescape(eq < 0 ? pair : pair.substring(0, eq));
            out.putIfAbsent(key, eq < 0 ? "" : unescape(pair.substring(eq + 1)));
        }
        return out;
    }

    private static String unescape(String text) {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        byte[] raw = text.getBytes(StandardCharsets.UTF_8);
        for (int i = 0; i < raw.length; i++) {
            byte b = raw[i];
            if (b == '+') {
                bytes.write(' ');
            } else if (b == '%' && i + 2 < raw.length && hex(raw[i + 1]) >= 0 && hex(raw[i + 2]) >= 0) {
                bytes.write(hex(raw[i + 1]) * 16 + hex(raw[i + 2]));
                i += 2;
            } else {
                bytes.write(b);
            }
        }
        return new String(bytes.toByteArray(), StandardCharsets.UTF_8);
    }

    private static int hex(byte b) {
        if (b >= '0' && b <= '9') {
            return b - '0';
        }
        if (b >= 'a' && b <= 'f') {
            return b - 'a' + 10;
        }
        if (b >= 'A' && b <= 'F') {
            return b - 'A' + 10;
        }
        return -1;
    }
}
