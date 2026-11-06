package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;

import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/**
 * Entry point of the library: append audit events, read them back, verify the chain and create checkpoints.
 * Implemented by {@link ProvanceLedger}; use this interface to mock the ledger in your tests.
 */
public interface LedgerService {

    /**
     * Serializes and canonicalizes {@code payload}, hands it to the writer and blocks until the sealed entry is
     * persisted.
     *
     * @throws IllegalArgumentException    {@code eventType} is blank or longer than 200 characters, or the payload
     *                                     cannot be serialized to canonical JSON
     * @throws LedgerOverloadedException   the queue is full and {@link QueueFullBehavior#FAIL_FAST} is configured
     * @throws LedgerUnavailableException  the writer is stopped, the store kept failing, or the thread was interrupted
     */
    LedgerEntry addEntry(String eventType, Object payload);

    /**
     * Same as {@link #addEntry(String, Object)} without blocking until the entry is persisted. Argument errors are
     * thrown immediately; store and capacity errors complete the future exceptionally. Dependent stages do not run
     * on the writer thread.
     */
    CompletableFuture<LedgerEntry> addEntryAsync(String eventType, Object payload);

    /** Returns the last entry of the chain, or empty if the ledger is empty. */
    Optional<LedgerEntry> getHead();

    /** Returns an entry by id, or empty. */
    Optional<LedgerEntry> getEntry(UUID id);

    /** Verifies the whole chain, without checkpoint. */
    LedgerVerificationResult verify();

    /** Verifies the chain. See {@link LedgerVerificationOptions} for checkpoint based verification. */
    LedgerVerificationResult verify(LedgerVerificationOptions options);

    /** Creates a signed checkpoint of the current head, or returns empty if the ledger is empty. */
    Optional<LedgerCheckpoint> createCheckpoint();

    /** Removes the payload of an entry while keeping the chain verifiable (GDPR erasure). */
    boolean redactPayload(UUID id);
}
