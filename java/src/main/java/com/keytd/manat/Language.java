package com.keytd.manat;

/** Payment page language. */
public enum Language {
    AZ("az"),
    EN("en"),
    RU("ru");

    private final String code;

    Language(String code) {
        this.code = code;
    }

    /** "az", "en" or "ru". */
    public String code() {
        return code;
    }
}
