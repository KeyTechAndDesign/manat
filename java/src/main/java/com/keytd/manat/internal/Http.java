package com.keytd.manat.internal;

import com.keytd.manat.ManatError;
import com.keytd.manat.http.HttpResponse;
import com.keytd.manat.http.JdkTransport;
import com.keytd.manat.http.Transport;

import java.io.IOException;
import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

/** Wraps a Transport for one adapter: encodes bodies, applies the timeout and turns failures into ManatError. */
public final class Http {
    public static final Duration DEFAULT_TIMEOUT = Duration.ofSeconds(30);
    public static final String VERSION = "0.2.1";
    /** Sent with every request so providers can tell the SDK apart from generic clients. */
    public static final String USER_AGENT = "keytd-manat/" + VERSION + " (java; +https://github.com/KeyTechAndDesign/manat)";

    private final String provider;
    private final Transport transport;
    private final Duration timeout;

    public Http(String provider, Transport transport, Duration timeout) {
        this.provider = provider;
        this.transport = transport == null ? new JdkTransport() : transport;
        this.timeout = timeout == null ? DEFAULT_TIMEOUT : timeout;
    }

    /** Status code and parsed JSON body (null for an empty body). */
    public static final class JsonResponse {
        public final int status;
        public final Object data;

        JsonResponse(int status, Object data) {
            this.status = status;
            this.data = data;
        }
    }

    /**
     * @param json JSON body, written unless null
     * @param form urlencoded body, used instead of json when not null
     */
    public HttpResponse send(String method, String url, Map<String, String> headers, Object json, Map<String, String> form) {
        Map<String, String> lower = new LinkedHashMap<>();
        lower.put("user-agent", USER_AGENT);
        headers.forEach((name, value) -> lower.put(name.toLowerCase(Locale.ROOT), value));
        String body = null;
        if (form != null) {
            body = Form.encode(form);
            lower.putIfAbsent("content-type", "application/x-www-form-urlencoded");
        } else if (json != null) {
            body = Json.stringify(json);
            lower.putIfAbsent("content-type", "application/json");
        }
        try {
            return transport.send(method, url, lower, body, timeout);
        } catch (ManatError error) {
            throw error;
        } catch (IOException | RuntimeException error) {
            String reason = error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage();
            throw new ManatError(ManatError.Code.NETWORK_ERROR, provider + ": " + method + " " + url + " " + reason, provider, null, null, null, error);
        }
    }

    /** Like send(), and parses the response as JSON. */
    public JsonResponse json(String method, String url, Map<String, String> headers, Object json, Map<String, String> form) {
        Map<String, String> withAccept = new LinkedHashMap<>();
        withAccept.put("accept", "application/json");
        headers.forEach((name, value) -> withAccept.put(name.toLowerCase(Locale.ROOT), value));
        HttpResponse response = send(method, url, withAccept, json, form);
        if (response.body().isEmpty()) {
            return new JsonResponse(response.status(), null);
        }
        try {
            return new JsonResponse(response.status(), Json.parse(response.body()));
        } catch (Json.ParseException error) {
            String excerpt = response.body().length() > 200 ? response.body().substring(0, 200) : response.body();
            throw new ManatError(ManatError.Code.NETWORK_ERROR, provider + ": expected JSON, got HTTP " + response.status() + " \"" + excerpt + "\"",
                    provider, null, null, response.body(), error);
        }
    }
}
