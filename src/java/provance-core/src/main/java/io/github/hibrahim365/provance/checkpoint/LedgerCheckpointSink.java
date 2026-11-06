package io.github.hibrahim365.provance.checkpoint;

import io.github.hibrahim365.provance.LedgerCheckpoint;

/**
 * Destination of automatic checkpoints. Implement it for the storage you trust: an object storage bucket with
 * retention lock (S3 Object Lock, Azure immutable blobs), WORM storage, a timestamping service, a log owned by
 * another team.
 *
 * <p>The value of a checkpoint depends entirely on where it goes. A sink the application can overwrite with the
 * same credentials it uses for the ledger adds little protection.
 */
@FunctionalInterface
public interface LedgerCheckpointSink {

    /** Publishes a checkpoint. Must not overwrite an existing checkpoint. */
    void publish(LedgerCheckpoint checkpoint) throws Exception;
}
