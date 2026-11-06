package io.github.hibrahim365.provance;

/**
 * The entry could not be written: the writer is stopped, or the store kept failing after all retries.
 * The entry may or may not have been persisted when the cause is a store failure.
 */
public class LedgerUnavailableException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public LedgerUnavailableException(String message) {
        super(message);
    }

    /** Creates the exception with a cause. */
    public LedgerUnavailableException(String message, Throwable cause) {
        super(message, cause);
    }
}
