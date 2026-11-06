package io.github.hibrahim365.provance.store;

import io.github.hibrahim365.provance.LedgerConcurrencyException;
import io.github.hibrahim365.provance.LedgerEntry;

import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.stream.Stream;

/**
 * Persistence of one ledger. Implementations do not compute hashes; they store what the writer sealed and
 * enforce uniqueness, which is what keeps the chain linear when several application instances write to the
 * same ledger. Implementations must be thread safe.
 *
 * <p>Failures other than {@link LedgerConcurrencyException} should be reported as unchecked exceptions, for
 * example {@link LedgerStoreException}. The writer treats them as transient and retries.
 */
public interface LedgerStore {

    /** Returns the entry with the highest sequence, or empty if the ledger is empty. */
    Optional<LedgerEntry> getHead();

    /**
     * Appends entries with contiguous, ascending sequences.
     *
     * <p>Must throw {@link LedgerConcurrencyException} when an entry with the same sequence or id already exists.
     * Entries must be inserted in order. If the call fails midway, a prefix of the batch may be persisted: the
     * writer detects that through {@link #getById(UUID)}.
     */
    void append(List<LedgerEntry> entries);

    /** Returns the entry with the given id, or empty. */
    Optional<LedgerEntry> getById(UUID id);

    /**
     * Streams entries with {@code sequence >= fromSequence} in ascending sequence order. The stream may hold
     * resources: close it, for example with try-with-resources.
     */
    Stream<LedgerEntry> read(long fromSequence);

    /**
     * Removes the payload of an entry and keeps everything else. The chain stays verifiable because only the
     * payload hash is signed.
     *
     * @return {@code true} if the entry exists
     */
    boolean redactPayload(UUID id);
}
