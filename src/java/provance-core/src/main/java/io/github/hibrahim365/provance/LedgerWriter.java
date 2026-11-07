package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.lang.System.Logger.Level;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;

/**
 * Background writer of one process. Seals queued entries in batches and appends them to the store.
 *
 * <p>Linearity across processes comes from the store: it rejects a sequence or id that already exists. On any
 * failure the writer reloads the head from the store, acknowledges the entries that were in fact persisted (looked
 * up by their fixed id) and reseals the others on top of the real head. Several application instances can
 * therefore write to the same ledger.
 *
 * <p>On close the writer stops accepting entries and drains the queue until the shutdown timeout. Entries that
 * cannot be written fail with {@link LedgerUnavailableException}.
 */
final class LedgerWriter implements Runnable {

    private static final System.Logger LOG = System.getLogger(LedgerWriter.class.getName());
    private static final long POLL_MILLIS = 100;

    private final BlockingQueue<PendingEntry> queue;
    private final LedgerStore store;
    private final LedgerSigner signer;
    private final ProvanceOptions options;
    private final Clock clock;
    private final String genesisHash;

    private volatile boolean accepting = true;
    private volatile boolean aborted;
    /** An entry is queued only while accepting, never after the writer stopped. */
    private final Object acceptLock = new Object();

    private Head head; // null: must be reloaded from the store before the next seal (writer thread only)

    private record Head(long sequence, String hash) {
    }

    LedgerWriter(LedgerStore store, LedgerSigner signer, ProvanceOptions options, Clock clock) {
        this.queue = new ArrayBlockingQueue<>(options.getQueueCapacity());
        this.store = store;
        this.signer = signer;
        this.options = options;
        this.clock = clock;
        this.genesisHash = options.effectiveGenesisHash();
    }

    /** Called by producers. Blocks when the queue is full and the behavior is {@link QueueFullBehavior#WAIT}. */
    void enqueue(PendingEntry entry) {
        try {
            while (!tryEnqueue(entry)) {
                Thread.sleep(POLL_MILLIS / 10 + 1);
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new LedgerUnavailableException("Interrupted while waiting for room in the PROVANCE queue; the entry was not queued.", e);
        }
    }

    /** Queues without blocking; {@code false} when the queue is full and the behavior is {@link QueueFullBehavior#WAIT}. */
    private boolean tryEnqueue(PendingEntry entry) {
        synchronized (acceptLock) {
            if (!accepting) {
                throw stopped();
            }
            if (queue.offer(entry)) {
                return true;
            }
            if (options.getQueueFullBehavior() == QueueFullBehavior.FAIL_FAST) {
                throw new LedgerOverloadedException("PROVANCE queue is full.");
            }
            return false;
        }
    }

    /** Stops accepting new entries. Entries already queued are still processed. */
    void stopAccepting() {
        synchronized (acceptLock) {
            accepting = false;
        }
    }

    /** Gives up: the current batch and every queued entry fail. */
    void abort() {
        synchronized (acceptLock) {
            accepting = false;
        }
        aborted = true;
    }

    @Override
    public void run() {
        LOG.log(Level.INFO, "PROVANCE writer started for ledger {0}.", options.getLedgerId());
        List<PendingEntry> batch = new ArrayList<>(options.getMaxBatchSize());
        try {
            while (!aborted) {
                PendingEntry first = queue.poll(POLL_MILLIS, TimeUnit.MILLISECONDS);
                if (first == null) {
                    if (!accepting && queue.isEmpty()) {
                        break;
                    }
                    continue;
                }
                batch.clear();
                batch.add(first);
                queue.drainTo(batch, options.getMaxBatchSize() - 1);

                try {
                    processBatch(batch);
                } catch (InterruptedException e) {
                    failAll(batch, new LedgerUnavailableException("PROVANCE writer stopped before the entry was written."));
                    break;
                } catch (RuntimeException e) {
                    LOG.log(Level.ERROR, "Unexpected error in the PROVANCE writer. The batch is failed and the writer continues.", e);
                    head = null;
                    failAll(batch, new LedgerUnavailableException("Unexpected PROVANCE writer error.", e));
                } catch (Error e) {
                    failAll(batch, new LedgerUnavailableException("Unexpected PROVANCE writer error.", e));
                    throw e;
                }
            }
        } catch (InterruptedException e) {
            // Shutdown.
        } finally {
            synchronized (acceptLock) {
                accepting = false;
            }
            PendingEntry leftover;
            while ((leftover = queue.poll()) != null) {
                leftover.fail(new LedgerUnavailableException("PROVANCE writer stopped before the entry was written."));
            }
            LOG.log(Level.INFO, "PROVANCE writer stopped for ledger {0}.", options.getLedgerId());
        }
    }

    private void processBatch(List<PendingEntry> batch) throws InterruptedException {
        List<PendingEntry> pending = new ArrayList<>(batch);
        int transientFailures = 0;
        int conflicts = 0;

        while (!pending.isEmpty()) {
            checkAborted();
            List<LedgerEntry> sealed = null;
            RuntimeException failure;
            boolean isConflict = false;

            try {
                Head current = head != null ? head : loadHead();
                sealed = seal(pending, current);
                store.append(sealed);

                for (int i = 0; i < pending.size(); i++) {
                    pending.get(i).succeed(sealed.get(i));
                }
                LedgerEntry last = sealed.get(sealed.size() - 1);
                head = new Head(last.sequence(), last.hash());
                return;
            } catch (LedgerConcurrencyException e) {
                failure = e;
                isConflict = true;
            } catch (RuntimeException e) {
                failure = e;
            }

            if (aborted || Thread.currentThread().isInterrupted()) {
                throw new InterruptedException("PROVANCE writer aborted");
            }

            // Whatever happened, our view of the head is no longer trustworthy.
            head = null;

            if (sealed != null) {
                acknowledgePersisted(pending);
                if (pending.isEmpty()) {
                    return;
                }
            }

            if (isConflict) {
                conflicts++;
                if (conflicts > options.getMaxConflictRetries()) {
                    LOG.log(Level.ERROR, "PROVANCE writer gave up after " + conflicts + " sequence conflicts.", failure);
                    failAll(pending, new LedgerUnavailableException(
                            "Gave up after " + conflicts + " sequence conflicts with other writers.", failure));
                    return;
                }
                LOG.log(Level.DEBUG, "Sequence conflict (another writer appended first). Reloading head and resealing {0} entries.",
                        pending.size());
                continue;
            }

            transientFailures++;
            if (transientFailures >= options.getMaxAppendAttempts()) {
                LOG.log(Level.ERROR, "PROVANCE append failed " + transientFailures + " times; failing " + pending.size() + " entries.", failure);
                failAll(pending, new LedgerUnavailableException("The ledger store failed " + transientFailures + " times.", failure));
                return;
            }

            Duration delay = backoff(transientFailures);
            LOG.log(Level.WARNING, "PROVANCE append failed (attempt " + transientFailures + "/" + options.getMaxAppendAttempts()
                    + "). Retrying in " + delay.toMillis() + " ms.", failure);
            Thread.sleep(delay.toMillis());
        }
    }

    /**
     * After a failed append, acknowledges the leading entries that the store did persist. Ids are fixed per pending
     * entry, so this also catches writes persisted by an earlier attempt.
     */
    private void acknowledgePersisted(List<PendingEntry> pending) {
        int acknowledged = 0;
        try {
            for (PendingEntry entry : pending) {
                Optional<LedgerEntry> stored = store.getById(entry.id);
                if (stored.isEmpty()) {
                    break;
                }
                entry.succeed(stored.get());
                acknowledged++;
            }
        } catch (RuntimeException e) {
            // If an entry was persisted after all, the next append reuses its id, the store reports a conflict and
            // this method runs again.
            LOG.log(Level.WARNING, "Could not check which entries were persisted after a failed append.", e);
        }

        if (acknowledged > 0) {
            LOG.log(Level.INFO, "{0} entries were persisted despite the reported failure.", acknowledged);
            pending.subList(0, acknowledged).clear();
        }
    }

    private Head loadHead() {
        Optional<LedgerEntry> stored = store.getHead();
        if (stored.isEmpty()) {
            return new Head(0, genesisHash);
        }
        LedgerEntry h = stored.get();
        if (!options.getLedgerId().equals(h.ledgerId())) {
            throw new IllegalStateException("The store contains ledger '" + h.ledgerId() + "' but this application is configured for '"
                    + options.getLedgerId() + "'. Use one table per ledger.");
        }
        return new Head(h.sequence(), h.hash());
    }

    private List<LedgerEntry> seal(List<PendingEntry> pending, Head current) {
        List<LedgerEntry> sealed = new ArrayList<>(pending.size());
        long sequence = current.sequence();
        String previousHash = current.hash();
        Instant timestamp = LedgerHashing.normalizeTimestamp(clock.instant());
        String keyId = signer.keyId();

        for (PendingEntry item : pending) {
            sequence++;
            if (sequence > ProvanceProtocol.MAX_SEQUENCE) {
                throw new IllegalStateException("The ledger reached the maximum sequence number.");
            }
            LedgerEntry draft = new LedgerEntry(ProvanceProtocol.FORMAT_VERSION, options.getLedgerId(), sequence, item.id,
                    timestamp, item.eventType, item.payloadJson, item.payloadHash, previousHash, keyId, "", "");
            String hash = LedgerHashing.computeEntryHash(draft);
            String signature = LedgerHashing.toHex(signer.sign(LedgerHashing.hashToBytes(hash)));
            sealed.add(draft.withHashAndSignature(hash, signature));
            previousHash = hash;
        }
        return sealed;
    }

    private Duration backoff(int failures) {
        double baseMs = options.getRetryBaseDelay().toMillis() * Math.pow(2, failures - 1);
        double jitter = baseMs * 0.2 * ThreadLocalRandom.current().nextDouble();
        return Duration.ofMillis((long) Math.min(baseMs + jitter, 2000));
    }

    private void checkAborted() throws InterruptedException {
        if (aborted) {
            throw new InterruptedException("PROVANCE writer aborted");
        }
    }

    private static void failAll(List<PendingEntry> entries, Throwable error) {
        for (PendingEntry entry : entries) {
            entry.fail(error);
        }
    }

    private static LedgerUnavailableException stopped() {
        return new LedgerUnavailableException("PROVANCE writer is stopping; the entry was not queued.");
    }
}
