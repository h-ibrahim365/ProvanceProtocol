package io.github.hibrahim365.provance.verification;

import io.github.hibrahim365.provance.LedgerCheckpoint;

/**
 * Options of a verification.
 *
 * @param trustedCheckpoint   a checkpoint obtained from a trusted place; when set, the ledger must contain that exact
 *                            entry, which detects truncation and full rewrites
 * @param startFromCheckpoint when {@code true} and a checkpoint is set, verification starts at the checkpoint instead
 *                            of the genesis entry (incremental verification)
 * @param throwOnFailure      throw {@link io.github.hibrahim365.provance.LedgerTamperedException} instead of returning a
 *                            failed result
 */
public record LedgerVerificationOptions(LedgerCheckpoint trustedCheckpoint, boolean startFromCheckpoint, boolean throwOnFailure,
                                        boolean acceptUnrecordedRedactions) {

    private static final LedgerVerificationOptions DEFAULTS = new LedgerVerificationOptions(null, false, false, false);

    /** Options without {@code acceptUnrecordedRedactions} (as in 0.1). */
    public LedgerVerificationOptions(LedgerCheckpoint trustedCheckpoint, boolean startFromCheckpoint, boolean throwOnFailure) {
        this(trustedCheckpoint, startFromCheckpoint, throwOnFailure, false);
    }

    /** Full verification from the genesis entry, without checkpoint. */
    public static LedgerVerificationOptions defaults() {
        return DEFAULTS;
    }

    /** Full verification that also requires the ledger to contain the entry described by {@code checkpoint}. */
    public static LedgerVerificationOptions withCheckpoint(LedgerCheckpoint checkpoint) {
        return new LedgerVerificationOptions(checkpoint, false, false);
    }

    /** Copy with {@code startFromCheckpoint} set. */
    public LedgerVerificationOptions startingFromCheckpoint() {
        return new LedgerVerificationOptions(trustedCheckpoint, true, throwOnFailure, acceptUnrecordedRedactions);
    }

    /** Copy with {@code throwOnFailure} set. */
    public LedgerVerificationOptions throwingOnFailure() {
        return new LedgerVerificationOptions(trustedCheckpoint, startFromCheckpoint, true, acceptUnrecordedRedactions);
    }

    /**
     * Copy that accepts payloads removed without a redaction record. Only for ledgers erased with 0.1, which wrote no
     * such record; with it, a payload deleted directly in the database is not detected.
     */
    public LedgerVerificationOptions acceptingUnrecordedRedactions() {
        return new LedgerVerificationOptions(trustedCheckpoint, startFromCheckpoint, throwOnFailure, true);
    }
}
