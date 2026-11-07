package io.github.hibrahim365.provance;

/**
 * An operation succeeded on the primary store but not on the archive, for example a payload erased in the primary
 * store that is still present in the archive.
 */
public class LedgerArchiveException extends RuntimeException {

    public LedgerArchiveException(String message, Throwable cause) {
        super(message, cause);
    }
}
