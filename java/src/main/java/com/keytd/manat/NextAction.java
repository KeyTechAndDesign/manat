package com.keytd.manat;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/** Where to send the customer after createPayment: a redirect (GET), an auto-submitted form, or nothing. */
public final class NextAction {
    public enum Type {
        /** Redirect the browser to {@link #url()}. */
        REDIRECT,
        /** Render {@link #fields()} as hidden inputs and auto-submit them to {@link #url()} with POST. */
        FORM,
        /** Nothing to do. */
        NONE
    }

    private final Type type;
    private final String url;
    private final Map<String, String> fields;

    private NextAction(Type type, String url, Map<String, String> fields) {
        this.type = type;
        this.url = url;
        this.fields = fields;
    }

    public static NextAction redirect(String url) {
        return new NextAction(Type.REDIRECT, url, Collections.emptyMap());
    }

    public static NextAction form(String url, Map<String, String> fields) {
        return new NextAction(Type.FORM, url, Collections.unmodifiableMap(new LinkedHashMap<>(fields)));
    }

    public static NextAction none() {
        return new NextAction(Type.NONE, null, Collections.emptyMap());
    }

    public Type type() {
        return type;
    }

    /** Null for {@link Type#NONE}. */
    public String url() {
        return url;
    }

    /** "POST" for a form, otherwise null. */
    public String method() {
        return type == Type.FORM ? "POST" : null;
    }

    /** Form fields in the order they were signed; empty unless {@link Type#FORM}. */
    public Map<String, String> fields() {
        return fields;
    }

    /** The spec's shape: {@code {type: "redirect", url}} or {@code {type: "form", url, method, fields}} or {@code {type: "none"}}. */
    public Map<String, Object> toMap() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("type", type.name().toLowerCase(java.util.Locale.ROOT));
        if (type != Type.NONE) {
            out.put("url", url);
        }
        if (type == Type.FORM) {
            out.put("method", "POST");
            out.put("fields", new LinkedHashMap<>(fields));
        }
        return out;
    }
}
