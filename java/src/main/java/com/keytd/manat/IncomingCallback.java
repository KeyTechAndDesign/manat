package com.keytd.manat;

import com.keytd.manat.internal.Form;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * What the merchant's HTTP handler received, for {@link Manat#verifyCallback}. Pass the raw body when you have it.
 *
 * <pre>{@code
 * IncomingCallback.of(request.getMethod())
 *     .withQuery(request.getParameterMap())   // Map<String, String[]> works, so do String and List values
 *     .withBody(rawBody);                      // or a parsed Map
 * }</pre>
 */
public final class IncomingCallback {
    private final String method;
    private final Map<String, List<String>> query;
    private final String bodyText;
    private final Map<String, Object> bodyMap;
    private final Map<String, List<String>> headers;

    private IncomingCallback(String method, Map<String, List<String>> query, String bodyText, Map<String, Object> bodyMap,
                             Map<String, List<String>> headers) {
        this.method = method;
        this.query = query;
        this.bodyText = bodyText;
        this.bodyMap = bodyMap;
        this.headers = headers;
    }

    public static IncomingCallback of(String method) {
        return new IncomingCallback(method, Collections.emptyMap(), null, null, Collections.emptyMap());
    }

    /** Query parameters; values may be String, String[] or a Collection. */
    public IncomingCallback withQuery(Map<String, ?> query) {
        return new IncomingCallback(method, multi(query, false), bodyText, bodyMap, headers);
    }

    /** The raw request body (urlencoded form or JSON text). */
    public IncomingCallback withBody(String body) {
        return new IncomingCallback(method, query, body, null, headers);
    }

    /** A body your framework already parsed: form fields, or a JSON object as nested maps and lists. */
    public IncomingCallback withBody(Map<String, ?> body) {
        return new IncomingCallback(method, query, null, body == null ? null : Collections.unmodifiableMap(new LinkedHashMap<String, Object>(body)), headers);
    }

    /** Request headers; names are matched case-insensitively. */
    public IncomingCallback withHeaders(Map<String, ?> headers) {
        return new IncomingCallback(method, query, bodyText, bodyMap, multi(headers, true));
    }

    public String method() {
        return method;
    }

    public Map<String, List<String>> query() {
        return query;
    }

    /** The first value of a query parameter, or null. */
    public String query(String name) {
        List<String> values = query.get(name);
        return values == null || values.isEmpty() ? null : values.get(0);
    }

    /** The raw body, or null when none was given or a parsed map was given instead. */
    public String bodyText() {
        return bodyText;
    }

    /** The parsed body, or null when none was given or raw text was given instead. */
    public Map<String, Object> bodyMap() {
        return bodyMap;
    }

    /** A field of an urlencoded text body or of a parsed body (first value of a list), when it is a string; or null. */
    public String bodyField(String name) {
        if (bodyText != null) {
            return Form.decode(bodyText).get(name);
        }
        if (bodyMap == null) {
            return null;
        }
        Object value = bodyMap.get(name);
        if (value instanceof List && !((List<?>) value).isEmpty()) {
            value = ((List<?>) value).get(0);
        } else if (value instanceof String[] && ((String[]) value).length > 0) {
            value = ((String[]) value)[0];
        }
        return value instanceof String ? (String) value : null;
    }

    /** Header names in lower case. */
    public Map<String, List<String>> headers() {
        return headers;
    }

    /** The spec's shape, for logging: {@code {method, query, body, headers}}. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("method", method);
        out.put("query", query);
        out.put("body", bodyText != null ? bodyText : bodyMap);
        out.put("headers", headers);
        return out;
    }

    private static Map<String, List<String>> multi(Map<String, ?> values, boolean lowerCaseKeys) {
        Map<String, List<String>> out = new LinkedHashMap<>();
        if (values == null) {
            return Collections.emptyMap();
        }
        for (Map.Entry<String, ?> entry : values.entrySet()) {
            List<String> list = new ArrayList<>();
            Object value = entry.getValue();
            if (value instanceof String) {
                list.add((String) value);
            } else if (value instanceof String[]) {
                Collections.addAll(list, (String[]) value);
            } else if (value instanceof Collection) {
                for (Object item : (Collection<?>) value) {
                    if (item != null) {
                        list.add(item.toString());
                    }
                }
            } else if (value != null) {
                list.add(value.toString());
            }
            String key = lowerCaseKeys ? entry.getKey().toLowerCase(java.util.Locale.ROOT) : entry.getKey();
            out.put(key, Collections.unmodifiableList(list));
        }
        return Collections.unmodifiableMap(out);
    }
}
