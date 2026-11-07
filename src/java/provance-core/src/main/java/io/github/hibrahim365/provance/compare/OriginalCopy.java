package io.github.hibrahim365.provance.compare;

/** Which copy of a changed entry is the original, judged by checking each copy on its own. */
public enum OriginalCopy {
    /** The archive copy verifies and the primary copy does not: the primary store was modified. */
    ARCHIVE,
    /** The primary copy verifies and the archive copy does not: the archive was modified. */
    PRIMARY,
    /** Both copies verify on their own: whoever changed one of them holds the signing key. */
    BOTH_VERIFY,
    /** Neither copy verifies on its own: both were modified. */
    NEITHER_VERIFIES
}
