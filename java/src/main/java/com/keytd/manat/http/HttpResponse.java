package com.keytd.manat.http;

/** Status code and body text of an HTTP response. */
public final class HttpResponse {
    private final int status;
    private final String body;

    public HttpResponse(int status, String body) {
        this.status = status;
        this.body = body == null ? "" : body;
    }

    public int status() {
        return status;
    }

    /** Never null. */
    public String body() {
        return body;
    }
}
