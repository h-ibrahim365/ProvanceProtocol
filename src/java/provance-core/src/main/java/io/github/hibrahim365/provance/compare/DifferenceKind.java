package io.github.hibrahim365.provance.compare;

/** What differs between the two copies of an entry. */
public enum DifferenceKind {
    /** Both copies have the entry, with different content. */
    CHANGED,
    /** The archive has the entry, the primary store does not. */
    MISSING_FROM_PRIMARY,
    /** The primary store has the entry and the archive does not, although the archive holds later entries. */
    MISSING_FROM_ARCHIVE,
    /** The payload was erased in one copy only. Not tampering. */
    REDACTED_IN_ONE_COPY
}
