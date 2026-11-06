package io.github.hibrahim365.provance.signing;

/** Outcome of a signature check. */
public enum SignatureCheck {

    /** The signature is valid for the given key. */
    VALID,

    /** The key is known but the signature does not match. */
    INVALID,

    /** The verifier does not know this key id. */
    UNKNOWN_KEY
}
