package com.keytd.manat.http;

import java.io.IOException;
import java.time.Duration;
import java.util.Map;

/**
 * Sends one HTTP request. The default is {@link JdkTransport} (java.net.http); pass your own to an adapter's
 * builder to use your framework's client or a fake in tests. Throw an IOException (or any RuntimeException)
 * on a network failure; the adapter reports it as a {@code network_error}.
 */
public interface Transport {
    /**
     * @param headers lower-case names
     * @param body    request body, or null
     */
    HttpResponse send(String method, String url, Map<String, String> headers, String body, Duration timeout) throws IOException;
}
