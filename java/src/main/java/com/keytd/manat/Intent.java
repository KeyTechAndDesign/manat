package com.keytd.manat;

/** What createPayment asks for. */
public enum Intent {
    /** Charge the card now. */
    PURCHASE,
    /** Hold the amount (pre-authorisation); finish with capture or release with cancel. */
    AUTHORIZE
}
