package com.keytd.manat.http;

import java.io.IOException;
import java.io.InterruptedIOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Map;

/** Default transport on java.net.http.HttpClient: HTTP/1.1, no redirects, TLS verified by the JDK. */
public final class JdkTransport implements Transport {
    private static final Duration CONNECT_TIMEOUT = Duration.ofSeconds(10);

    private final HttpClient client;

    public JdkTransport() {
        this(HttpClient.newBuilder()
                .version(HttpClient.Version.HTTP_1_1)
                .followRedirects(HttpClient.Redirect.NEVER)
                .connectTimeout(CONNECT_TIMEOUT)
                .build());
    }

    /** Uses a client you configured (proxy, SSL context, executor…). */
    public JdkTransport(HttpClient client) {
        this.client = client;
    }

    @Override
    public HttpResponse send(String method, String url, Map<String, String> headers, String body, Duration timeout) throws IOException {
        HttpRequest.Builder request = HttpRequest.newBuilder(URI.create(url))
                .timeout(timeout)
                .method(method, body == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8));
        headers.forEach(request::header);
        try {
            java.net.http.HttpResponse<String> response = client.send(request.build(), java.net.http.HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
            return new HttpResponse(response.statusCode(), response.body());
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
            InterruptedIOException error = new InterruptedIOException("request interrupted");
            error.initCause(interrupted);
            throw error;
        }
    }
}
