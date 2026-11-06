package io.github.hibrahim365.provance;

/**
 * Constants of the PROVANCE ledger format (see {@code docs/SPEC.md}).
 */
public final class ProvanceProtocol {

    /** Version of the signed envelope written by this library (field {@code v}). */
    public static final int FORMAT_VERSION = 1;

    /** Largest sequence number allowed. Sequences are JSON numbers, so they must stay exact as IEEE 754 doubles. */
    public static final long MAX_SEQUENCE = (1L << 53) - 1;

    /** Prefix hashed with the ledger id to derive the default genesis hash. */
    public static final String GENESIS_PREFIX = "provance:genesis:v1:";

    private ProvanceProtocol() {
    }
}
