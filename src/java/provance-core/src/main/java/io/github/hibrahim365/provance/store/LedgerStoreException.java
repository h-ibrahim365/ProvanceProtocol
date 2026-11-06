package io.github.hibrahim365.provance.store;

/** A store operation failed (database unavailable, timeout, ...). The writer retries appends that fail this way. */
public class LedgerStoreException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public LedgerStoreException(String message) {
        super(message);
    }

    /** Creates the exception with a cause. */
    public LedgerStoreException(String message, Throwable cause) {
        super(message, cause);
    }
}
