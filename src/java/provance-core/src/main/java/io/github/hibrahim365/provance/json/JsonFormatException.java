package io.github.hibrahim365.provance.json;

/**
 * The input is not valid JSON, or cannot be canonicalized (duplicate member names, non finite number,
 * invalid Unicode).
 */
public final class JsonFormatException extends IllegalArgumentException {

    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public JsonFormatException(String message) {
        super(message);
    }

    /** Creates the exception with a cause. */
    public JsonFormatException(String message, Throwable cause) {
        super(message, cause);
    }
}
