package io.github.hibrahim365.provance.compare;

import java.util.List;

/**
 * A difference at one sequence, or a range of sequences.
 *
 * @param original which copy is the original, for {@link DifferenceKind#CHANGED}; {@code null} otherwise
 */
public record EntryDifference(long sequence, Long throughSequence, DifferenceKind kind, OriginalCopy original,
                              String eventType, List<FieldDifference> fields) {

    public EntryDifference {
        fields = List.copyOf(fields);
    }

    static EntryDifference of(long sequence, DifferenceKind kind, String eventType) {
        return new EntryDifference(sequence, null, kind, null, eventType, List.of());
    }

    /** {@code false} only for {@link DifferenceKind#REDACTED_IN_ONE_COPY}. */
    public boolean isTampering() {
        return kind != DifferenceKind.REDACTED_IN_ONE_COPY;
    }

    /** One line summary. */
    public String summary() {
        String event = eventType == null ? "" : " (" + eventType + ")";
        switch (kind) {
            case CHANGED:
                String where;
                if (original == OriginalCopy.ARCHIVE) {
                    where = "in the primary store (the archive copy verifies, the primary copy does not)";
                } else if (original == OriginalCopy.PRIMARY) {
                    where = "in the archive (the primary copy verifies, the archive copy does not)";
                } else if (original == OriginalCopy.BOTH_VERIFY) {
                    where = "(both copies verify on their own: whoever changed it holds the signing key)";
                } else {
                    where = "(neither copy verifies on its own: both were modified)";
                }
                return "Entry " + sequence + event + " was changed " + where + ".";
            case MISSING_FROM_PRIMARY:
                if (throughSequence != null && throughSequence != sequence) {
                    return "Entries " + sequence + " to " + throughSequence + " are in the archive but missing from the primary store.";
                }
                return "Entry " + sequence + event + " is in the archive but missing from the primary store.";
            case MISSING_FROM_ARCHIVE:
                return "Entry " + sequence + event + " is in the primary store but missing from the archive.";
            default:
                return "Entry " + sequence + event + ": the payload was erased in one copy only.";
        }
    }
}
