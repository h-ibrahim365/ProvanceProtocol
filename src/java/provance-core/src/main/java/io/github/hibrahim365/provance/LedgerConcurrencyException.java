package io.github.hibrahim365.provance;

/**
 * Thrown by {@link io.github.hibrahim365.provance.store.LedgerStore#append} when an entry with the same sequence or
 * id already exists. The writer handles it by reloading the head; applications rarely see it.
 */
public final class LedgerConcurrencyException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public LedgerConcurrencyException(String message) {
        super(message);
    }

    /** Creates the exception with a cause. */
    public LedgerConcurrencyException(String message, Throwable cause) {
        super(message, cause);
    }
}
