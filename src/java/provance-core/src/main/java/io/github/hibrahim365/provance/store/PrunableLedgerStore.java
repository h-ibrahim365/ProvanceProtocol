package io.github.hibrahim365.provance.store;

/**
 * A store that can remove its oldest entries. Required by retention ({@code ProvanceLedger.applyRetention}); the
 * in-memory and JDBC stores implement it.
 */
public interface PrunableLedgerStore extends LedgerStore {

    /** Deletes every entry with a sequence lower than {@code sequence}; returns how many were deleted. */
    long deleteBefore(long sequence);
}
