package io.github.hibrahim365.provance;

/**
 * Result of {@link ProvanceLedger#applyRetention}.
 *
 * @param applied        {@code true} when entries were removed
 * @param message        what happened, or why nothing was removed
 * @param removedEntries entries removed from the primary store
 * @param firstSequence  first sequence left in the primary store
 * @param record         the retention record appended before the removal, or {@code null}
 * @param checkpoint     checkpoint of the head after the removal, already published to the sinks, or {@code null}
 */
public record RetentionResult(boolean applied, String message, long removedEntries, long firstSequence,
                              LedgerEntry record, LedgerCheckpoint checkpoint) {

    static RetentionResult skipped(String message, long firstSequence) {
        return new RetentionResult(false, message, 0, firstSequence, null, null);
    }
}
