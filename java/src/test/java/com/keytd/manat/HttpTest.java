package com.keytd.manat;

import com.keytd.manat.http.HttpResponse;
import com.keytd.manat.internal.Http;
import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

class HttpTest {
    @Test
    void identifiesTheSdkInEveryRequest() {
        Map<String, String> sent = new HashMap<>();
        new Http("test", (method, url, headers, body, timeout) -> {
            sent.putAll(headers);
            return new HttpResponse(200, "{}");
        }, null).json("GET", "https://example.com", Collections.emptyMap(), null, null);
        assertEquals("keytd-manat/" + Http.VERSION + " (java; +https://github.com/KeyTechAndDesign/manat)", sent.get("user-agent"));
    }

    @Test
    void reportsThePomVersion() throws IOException {
        String pom = new String(Files.readAllBytes(Paths.get("pom.xml")), StandardCharsets.UTF_8);
        Matcher version = Pattern.compile("<artifactId>manat</artifactId>\\s*<version>([^<]+)</version>").matcher(pom);
        assertTrue(version.find());
        assertEquals(version.group(1), Http.VERSION);
    }
}
