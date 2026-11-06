package io.github.hibrahim365.provance.verification;

import java.util.UUID;

/**
 * Result of a ledger verification.
 *
 * @param valid            {@code true} when every verified entry is consistent
 * @param failure          reason of the failure, {@link VerificationFailure#NONE} when valid
 * @param message          human readable explanation
 * @param failedAtSequence sequence of the first entry that failed, or {@code null}
 * @param failedEntryId    id of the first entry that failed, or {@code null}
 * @param entriesVerified  number of entries checked
 * @param redactedPayloads number of checked entries whose payload was redacted
 * @param lastSequence     sequence of the last valid entry (0 when none)
 * @param lastHash         hash of the last valid entry, or {@code null}. Compare it with an external checkpoint:
 *                         without one, removing entries from the end of the ledger cannot be detected.
 */
public record LedgerVerificationResult(
        boolean valid,
        VerificationFailure failure,
        String message,
        Long failedAtSequence,
        UUID failedEntryId,
        long entriesVerified,
        long redactedPayloads,
        long lastSequence,
        String lastHash) {
}
