package io.github.hibrahim365.provance.verification;

/** Reason of a verification failure. */
public enum VerificationFailure {

    /** No failure. */
    NONE,

    /** A sequence number is missing, duplicated or out of order (deleted or inserted entry). */
    SEQUENCE_GAP,

    /** {@code previousHash} does not match the hash of the previous entry. */
    BROKEN_LINK,

    /** The recomputed envelope hash differs from the stored hash (a signed field was modified). */
    HASH_MISMATCH,

    /** The payload does not match {@code payloadHash}. */
    PAYLOAD_HASH_MISMATCH,

    /** The signature is not valid for the declared key. */
    INVALID_SIGNATURE,

    /** The entry was signed with a key the verifier does not know. */
    UNKNOWN_KEY,

    /** The entry belongs to another ledger. */
    LEDGER_ID_MISMATCH,

    /** The entry uses a format version this library does not understand. */
    UNSUPPORTED_FORMAT,

    /** The trusted checkpoint is invalid (signature, ledger id) or does not match the stored entry at its sequence. */
    CHECKPOINT_MISMATCH,

    /** The ledger ends before the sequence of the trusted checkpoint: entries were removed from the end. */
    TRUNCATED,

    /** A payload is missing without a redaction record: it was erased in the database, not through the library. */
    UNRECORDED_REDACTION
}
