package io.github.hibrahim365.provance;

/**
 * The queue is full and {@link QueueFullBehavior#FAIL_FAST} is configured. Map it to HTTP 503 or 429.
 */
public final class LedgerOverloadedException extends LedgerUnavailableException {

    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public LedgerOverloadedException(String message) {
        super(message);
    }
}
