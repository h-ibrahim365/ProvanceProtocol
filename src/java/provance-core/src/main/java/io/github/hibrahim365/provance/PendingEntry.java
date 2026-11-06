package io.github.hibrahim365.provance;

import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/**
 * An entry prepared by a producer (payload already canonicalized and hashed) waiting for the writer.
 * The id is fixed here so that the writer can recognize the entry in the store after an ambiguous failure.
 */
final class PendingEntry {

    final UUID id = UUID.randomUUID();
    final String eventType;
    final String payloadJson;
    final String payloadHash;
    final CompletableFuture<LedgerEntry> completion = new CompletableFuture<>();

    PendingEntry(String eventType, String payloadJson, String payloadHash) {
        this.eventType = eventType;
        this.payloadJson = payloadJson;
        this.payloadHash = payloadHash;
    }

    void succeed(LedgerEntry entry) {
        completion.complete(entry);
    }

    void fail(Throwable error) {
        completion.completeExceptionally(error);
    }
}
