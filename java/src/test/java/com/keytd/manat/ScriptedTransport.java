package com.keytd.manat;

import com.keytd.manat.http.HttpResponse;
import com.keytd.manat.http.Transport;
import com.keytd.manat.internal.Form;
import com.keytd.manat.internal.Json;
import com.keytd.manat.internal.Values;

import java.time.Duration;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;

/** Serves a vector's {@code http} steps in order and checks that each request matches {@code expect}. */
final class ScriptedTransport implements Transport {
    private final List<Object> steps;
    private int index;

    ScriptedTransport(List<Object> steps) {
        this.steps = steps == null ? Collections.emptyList() : steps;
    }

    @Override
    public HttpResponse send(String method, String url, Map<String, String> headers, String body, Duration timeout) {
        int n = index + 1;
        Object step = index < steps.size() ? steps.get(index) : null;
        index++;
        assertNotNull(step, "unexpected request #" + n + ": " + method + " " + url);
        Map<String, Object> expect = Values.map(Values.get(step, "expect"));
        assertEquals(expect.get("method"), method, "request #" + n + " method");
        assertEquals(expect.get("url"), url, "request #" + n + " url");
        Map<String, Object> expectedHeaders = Values.map(expect.get("headers"));
        if (expectedHeaders != null) {
            expectedHeaders.forEach((name, value) -> assertEquals(value, headers.get(name.toLowerCase(Locale.ROOT)), "request #" + n + " header " + name));
        }
        if (expect.containsKey("json")) {
            Expect.equal(expect.get("json"), Json.parse(body), "request #" + n + " body");
        }
        if (expect.containsKey("form")) {
            Expect.equal(expect.get("form"), Form.decode(body), "request #" + n + " form");
        }
        Map<String, Object> respond = Values.map(Values.get(step, "respond"));
        String text = respond.containsKey("json") ? Json.stringify(respond.get("json")) : respond.get("text") == null ? "" : (String) respond.get("text");
        return new HttpResponse(Values.integer(respond.get("status")).intValue(), text);
    }

    void assertDone() {
        assertEquals(steps.size(), index, "all scripted requests were made");
    }
}
