package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.verification.LedgerVerificationResult;

/**
 * Thrown by {@code verify} when {@code throwOnFailure} is set and verification fails.
 */
public final class LedgerTamperedException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    private final transient LedgerVerificationResult result;

    /** Creates the exception from a failed verification result. */
    public LedgerTamperedException(LedgerVerificationResult result) {
        super(result.message());
        this.result = result;
    }

    /** The verification result that caused the exception. */
    public LedgerVerificationResult result() {
        return result;
    }
}
