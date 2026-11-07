package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.checkpoint.FileCheckpointSink;
import io.github.hibrahim365.provance.compare.EntryDifference;
import io.github.hibrahim365.provance.compare.LedgerComparer;
import io.github.hibrahim365.provance.compare.LedgerComparison;
import io.github.hibrahim365.provance.store.PrunableLedgerStore;
import io.github.hibrahim365.provance.checkpoint.LedgerCheckpointSink;
import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;
import io.github.hibrahim365.provance.json.JsonFormatException;
import io.github.hibrahim365.provance.json.PayloadSerializer;
import io.github.hibrahim365.provance.json.SimplePayloadSerializer;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.store.LedgerStore;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.LedgerVerifier;

import java.lang.System.Logger.Level;
import java.nio.file.Path;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Iterator;
import java.util.stream.Stream;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.ForkJoinPool;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;
import java.util.function.Function;

/**
 * A PROVANCE ledger: append audit events, verify the chain, create checkpoints.
 *
 * <pre>{@code
 * ProvanceLedger ledger = ProvanceLedger.builder()
 *         .ledgerId("orders-api-prod")
 *         .secretKey(System.getenv("PROVANCE_SECRET_KEY"))
 *         .store(new JdbcLedgerStore(dataSource))
 *         .build();
 *
 * ledger.addEntry("ORDER_REFUNDED", AuditedPayload.of(userId, "Refund issued", Map.of("orderId", id)));
 * LedgerVerificationResult result = ledger.verify();
 * }</pre>
 *
 * <p>One instance per ledger and per process; it is thread safe. It owns a background writer thread: call
 * {@link #close()} on shutdown so queued entries are written (Spring does it for you).
 */
public final class ProvanceLedger implements LedgerService, AutoCloseable {

    private static final System.Logger LOG = System.getLogger(ProvanceLedger.class.getName());
    private static final int MAX_EVENT_TYPE_LENGTH = 200;

    private final ProvanceOptions options;
    private final LedgerStore store;
    private final LedgerSigner signer;
    private final PayloadSerializer serializer;
    private final Clock clock;
    private final Executor callbackExecutor;
    private final LedgerVerifier verifier;
    private final LedgerWriter writer;
    private final Thread writerThread;
    private final CheckpointPublisher checkpoints;
    private final AtomicBoolean closed = new AtomicBoolean();
    private final LedgerComparer comparer;
    private final ArchiveCopier copier;
    private final List<LedgerCheckpointSink> sinks;
    private final Object retentionLock = new Object();
    private final Thread retentionThread;
    private volatile boolean retentionStopping;

    private ProvanceLedger(Builder b) {
        this.options = b.options;
        this.store = b.store;
        this.signer = b.signer != null
                ? b.signer
                : new HmacLedgerSigner(options.getSigningKeyId(), options.getSecretKey(), options.getRetiredKeys());
        this.serializer = b.serializer;
        this.clock = b.clock;
        this.callbackExecutor = b.callbackExecutor;
        this.verifier = new LedgerVerifier(store, signer, options.getLedgerId(), options.effectiveGenesisHash());
        this.comparer = new LedgerComparer(signer, options.getLedgerId());
        if (b.archive != null && b.archive == store) {
            throw new IllegalStateException("The archive of ledger '" + options.getLedgerId() + "' is the same store as its primary store.");
        }
        this.copier = b.archive == null ? null : new ArchiveCopier(store, b.archive, comparer, options, clock);
        this.sinks = List.copyOf(b.sinks);
        this.writer = new LedgerWriter(store, signer, options, clock);
        this.writerThread = new Thread(writer, "provance-writer-" + options.getLedgerId());
        this.writerThread.setDaemon(true);
        this.checkpoints = new CheckpointPublisher(this, b.sinks, options.getCheckpointInterval());
        if (copier != null) {
            writer.onAppended = copier::signal;
            copier.start();
        }
        this.writerThread.start();
        this.checkpoints.start(options.getLedgerId());
        if (options.getRetentionKeepFor() != null) {
            retentionThread = new Thread(this::runRetention, "provance-retention-" + options.getLedgerId());
            retentionThread.setDaemon(true);
            retentionThread.start();
        } else {
            retentionThread = null;
        }
    }

    /** Starts the configuration of a ledger. */
    public static Builder builder() {
        return new Builder();
    }

    @Override
    public LedgerEntry addEntry(String eventType, Object payload) {
        PendingEntry pending = prepare(eventType, payload);
        writer.enqueue(pending);
        try {
            return pending.completion.get();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new LedgerUnavailableException("Interrupted while waiting for the audit entry; it may still be written.", e);
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException) {
                throw (RuntimeException) cause;
            }
            throw new LedgerUnavailableException("The audit entry could not be written.", cause);
        }
    }

    @Override
    public CompletableFuture<LedgerEntry> addEntryAsync(String eventType, Object payload) {
        PendingEntry pending = prepare(eventType, payload);
        try {
            writer.enqueue(pending);
        } catch (LedgerUnavailableException e) {
            return CompletableFuture.failedFuture(e);
        }
        // Dependent stages run on the callback executor, never on the writer thread.
        return pending.completion.thenApplyAsync(Function.identity(), callbackExecutor);
    }

    @Override
    public Optional<LedgerEntry> getHead() {
        return store.getHead();
    }

    @Override
    public Optional<LedgerEntry> getEntry(UUID id) {
        return store.getById(id);
    }

    @Override
    public LedgerVerificationResult verify() {
        return verifier.verify();
    }

    @Override
    public LedgerVerificationResult verify(LedgerVerificationOptions verificationOptions) {
        return verifier.verify(verificationOptions);
    }

    @Override
    public Optional<LedgerCheckpoint> createCheckpoint() {
        Optional<LedgerEntry> head = store.getHead();
        if (head.isEmpty()) {
            return Optional.empty();
        }
        LedgerEntry h = head.get();
        LedgerCheckpoint unsigned = new LedgerCheckpoint(ProvanceProtocol.FORMAT_VERSION, h.ledgerId(), h.sequence(), h.hash(),
                LedgerHashing.normalizeTimestamp(clock.instant()), signer.keyId(), "");
        byte[] digest = LedgerHashing.hashToBytes(LedgerHashing.computeCheckpointHash(unsigned));
        return Optional.of(unsigned.withSignature(LedgerHashing.toHex(signer.sign(digest))));
    }

    /**
     * Removes the payload of an entry while keeping the chain verifiable (GDPR erasure). A signed redaction record is
     * appended first, so verification can tell this erasure from a payload deleted directly in the database. With an
     * archive, the payload is removed from the archive copy too.
     *
     * @return {@code true} if the entry exists, in the primary store or in the archive
     * @throws LedgerArchiveException the payload was removed from the primary store but not from the archive
     */
    @Override
    public boolean redactPayload(UUID id) {
        Objects.requireNonNull(id, "id");
        Optional<LedgerEntry> entry = store.getById(id);
        Optional<LedgerEntry> archived = Optional.empty();
        if (copier != null) {
            try {
                archived = copier.archive.getById(id);
            } catch (RuntimeException e) {
                if (entry.isEmpty()) {
                    throw e;
                }
                LOG.log(Level.WARNING, "PROVANCE could not read the archive before erasing entry " + id + ".", e);
            }
        }
        Optional<LedgerEntry> known = entry.isPresent() ? entry : archived;
        if (known.isEmpty()) {
            return false;
        }
        if (LibraryRecords.isReserved(known.get().eventType())) {
            throw new IllegalArgumentException("Entries written by the library cannot be redacted.");
        }
        // A payload already missing was not erased through this method: recording it now would make a deletion done
        // in the database look authorized.
        boolean primaryHas = entry.isPresent() && entry.get().payloadJson() != null;
        boolean archiveHas = archived.isPresent() && archived.get().payloadJson() != null;
        if (primaryHas || archiveHas) {
            addLibraryEntry(LibraryRecords.REDACTION_EVENT, LibraryRecords.redactionPayload(known.get().sequence(), id)); // record first
        }
        if (primaryHas) {
            store.redactPayload(id);
        }
        if (copier != null && (archiveHas || archived.isEmpty())) {
            try {
                copier.archive.redactPayload(id); // false: not copied yet; the copy carries the erased payload
            } catch (RuntimeException e) {
                throw new LedgerArchiveException("The payload of entry " + id
                        + " was erased in the primary store but not in the archive: " + e.getMessage(), e);
            }
        }
        return true;
    }

    /** State of the copy to the archive, or empty without an archive. */
    public Optional<ArchiveStatus> archiveStatus() {
        return copier == null ? Optional.empty() : Optional.of(copier.status());
    }

    /** Compares the primary store with the archive, field by field, after copying the latest entries. */
    public LedgerComparison compareWithArchive() {
        return compareWithArchive(1000);
    }

    /** Compares the primary store with the archive, listing at most {@code maxDifferences} differences. */
    public LedgerComparison compareWithArchive(int maxDifferences) {
        if (copier == null) {
            throw new IllegalStateException("No archive is configured for ledger '" + ledgerId() + "'. Call archive(...) on the builder.");
        }
        try {
            copier.copy();
        } catch (RuntimeException e) {
            LOG.log(Level.WARNING, "PROVANCE could not copy the latest entries to the archive before comparing.", e);
        }
        return comparer.compare(store, copier.archive, maxDifferences);
    }

    /** The comparer used by {@link #compareWithArchive()}, for example to compare with an export. */
    public LedgerComparer comparer() {
        return comparer;
    }

    /**
     * Removes entries older than {@code keepFor} from the primary store, keeping the ledger verifiable (see
     * {@code docs/RETENTION.md}). Nothing is removed if the primary store does not verify, or if the archive does not
     * hold an identical copy (unless {@code retentionRequireArchive} is {@code false}).
     *
     * @throws UnsupportedOperationException the store cannot delete entries (it is not a {@link PrunableLedgerStore})
     */
    public RetentionResult applyRetention(Duration keepFor) {
        if (keepFor == null || keepFor.isNegative() || keepFor.isZero()) {
            throw new IllegalArgumentException("keepFor must be positive.");
        }
        if (!(store instanceof PrunableLedgerStore prunable)) {
            throw new UnsupportedOperationException("The store " + store.getClass().getSimpleName()
                    + " cannot delete entries (it does not implement PrunableLedgerStore).");
        }
        synchronized (retentionLock) {
            return applyRetention(prunable, keepFor);
        }
    }

    private RetentionResult applyRetention(PrunableLedgerStore prunable, Duration keepFor) {
        LedgerStore archive = copier == null ? null : copier.archive;
        if (archive == null && options.isRetentionRequireArchive()) {
            return RetentionResult.skipped("No archive is configured, so nothing is removed. Call archive(...), or set "
                    + "retentionRequireArchive to false if you keep a copy another way.", 0);
        }
        LedgerVerificationResult verification = verifier.verify();
        if (!verification.valid()) {
            return RetentionResult.skipped("The primary store does not verify, so nothing is removed: " + verification.message(), 0);
        }
        Instant cutoff = LedgerHashing.normalizeTimestamp(clock.instant()).minus(keepFor);
        LedgerEntry boundary = null;
        long first = 0;
        try (Stream<LedgerEntry> entries = store.read(1)) {
            for (LedgerEntry e : (Iterable<LedgerEntry>) entries::iterator) {
                if (first == 0) {
                    first = e.sequence();
                }
                if (!e.timestamp().isBefore(cutoff)) {
                    break;
                }
                boundary = e;
            }
        }
        if (boundary == null || boundary.sequence() <= first) {
            return RetentionResult.skipped("No entry older than " + keepFor + " can be removed.", first);
        }
        if (archive != null) {
            long archived = copier.copy();
            if (archived < boundary.sequence()) {
                return RetentionResult.skipped("The archive holds entries up to " + archived + " only; entries before "
                        + boundary.sequence() + " are kept until they are copied.", first);
            }
            String mismatch = firstMismatch(archive, first, boundary.sequence());
            if (mismatch != null) {
                return RetentionResult.skipped("The archive does not hold an identical copy, so nothing is removed: " + mismatch, first);
            }
        }
        LedgerEntry record = addLibraryEntry(LibraryRecords.RETENTION_EVENT,
                LibraryRecords.retentionPayload(boundary.sequence(), boundary.hash()));
        long removed = prunable.deleteBefore(boundary.sequence());
        LedgerCheckpoint checkpoint = createCheckpoint().orElse(null);
        if (checkpoint != null) {
            for (LedgerCheckpointSink sink : sinks) {
                try {
                    sink.publish(checkpoint);
                } catch (Exception e) {
                    LOG.log(Level.ERROR, "PROVANCE could not publish the checkpoint taken after retention.", e);
                }
            }
        }
        if (copier != null) {
            copier.signal();
        }
        return new RetentionResult(true, removed + " entries before sequence " + boundary.sequence()
                + " were removed from the primary store.", removed, boundary.sequence(), record, checkpoint);
    }

    private String firstMismatch(LedgerStore archive, long first, long through) {
        try (Stream<LedgerEntry> primaryEntries = store.read(first); Stream<LedgerEntry> archivedEntries = archive.read(first)) {
            Iterator<LedgerEntry> copies = archivedEntries.iterator();
            for (LedgerEntry e : (Iterable<LedgerEntry>) primaryEntries::iterator) {
                if (e.sequence() > through) {
                    break;
                }
                if (!copies.hasNext()) {
                    return "entry " + e.sequence() + " is missing from the archive.";
                }
                LedgerEntry copy = copies.next();
                if (copy.sequence() != e.sequence()) {
                    return "the archive has entry " + copy.sequence() + " where entry " + e.sequence() + " was expected.";
                }
                Optional<EntryDifference> difference = comparer.compareEntry(copy, e);
                if (difference.isPresent() && difference.get().isTampering()) {
                    return difference.get().summary();
                }
            }
        }
        return null;
    }

    private void runRetention() {
        Duration keepFor = options.getRetentionKeepFor();
        long interval = options.getRetentionInterval().toMillis();
        long wait = Math.min(interval, 60_000); // first run soon after start
        while (!retentionStopping) {
            try {
                synchronized (retentionLock) {
                    retentionLock.wait(wait);
                }
            } catch (InterruptedException e) {
                return;
            }
            if (retentionStopping) {
                return;
            }
            try {
                RetentionResult result = applyRetention(keepFor);
                LOG.log(result.applied() ? Level.INFO : Level.DEBUG, "PROVANCE retention on ledger " + ledgerId() + ": " + result.message());
            } catch (RuntimeException e) {
                LOG.log(Level.ERROR, "PROVANCE retention failed on ledger " + ledgerId() + "; it is retried at the next interval.", e);
            }
            wait = interval;
        }
    }

    private LedgerEntry addLibraryEntry(String eventType, String payloadJson) {
        if (closed.get()) {
            throw new LedgerUnavailableException("PROVANCE ledger is closed; the entry was not queued.");
        }
        PendingEntry pending = new PendingEntry(eventType, payloadJson, LedgerHashing.sha256Hex(payloadJson));
        writer.enqueue(pending);
        try {
            return pending.completion.get();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new LedgerUnavailableException("Interrupted while waiting for the library entry; it may still be written.", e);
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException runtime) {
                throw runtime;
            }
            throw new LedgerUnavailableException("The library entry could not be written.", cause);
        }
    }

    /** The verifier used by {@link #verify()}, for example to check a checkpoint received from elsewhere. */
    public LedgerVerifier verifier() {
        return verifier;
    }

    /** The configured ledger id. */
    public String ledgerId() {
        return options.getLedgerId();
    }

    /**
     * Stops accepting entries, writes the queued ones (up to the shutdown timeout) and stops the background threads.
     * Entries still queued after the timeout fail with {@link LedgerUnavailableException}. Idempotent.
     */
    @Override
    public void close() {
        if (!closed.compareAndSet(false, true)) {
            return;
        }
        retentionStopping = true;
        if (retentionThread != null) {
            synchronized (retentionLock) {
                retentionLock.notifyAll();
            }
            try {
                retentionThread.join(5000);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
        checkpoints.stop();
        writer.stopAccepting();
        try {
            writerThread.join(Math.max(1, options.getShutdownTimeout().toMillis()));
            if (writerThread.isAlive()) {
                LOG.log(Level.WARNING, "PROVANCE writer did not drain its queue before the shutdown timeout; remaining entries fail.");
                writer.abort();
                writerThread.interrupt();
                writerThread.join(TimeUnit.SECONDS.toMillis(5));
            }
        } catch (InterruptedException e) {
            writer.abort();
            writerThread.interrupt();
            Thread.currentThread().interrupt();
        }
        if (copier != null) {
            copier.stop(Math.max(1, options.getShutdownTimeout().toMillis())); // after the writer: copies its last entries
        }
    }

    private PendingEntry prepare(String eventType, Object payload) {
        if (eventType == null || eventType.isBlank()) {
            throw new IllegalArgumentException("Event type is required.");
        }
        if (eventType.length() > MAX_EVENT_TYPE_LENGTH) {
            throw new IllegalArgumentException("Event type is limited to " + MAX_EVENT_TYPE_LENGTH + " characters.");
        }
        if (LibraryRecords.isReserved(eventType)) {
            throw new IllegalArgumentException("Event types starting with 'provance.' are reserved for the library.");
        }
        if (payload == null) {
            throw new IllegalArgumentException("Payload is required.");
        }
        if (closed.get()) {
            throw new LedgerUnavailableException("PROVANCE ledger is closed; the entry was not queued.");
        }
        String payloadJson;
        try {
            payloadJson = JsonCanonicalizer.serialize(payload, serializer);
        } catch (RuntimeException e) {
            throw new JsonFormatException("Payload of type " + payload.getClass().getName() + " cannot be serialized to JSON: "
                    + e.getMessage(), e);
        }
        return new PendingEntry(eventType, payloadJson, LedgerHashing.sha256Hex(payloadJson));
    }

    /** Configuration of a {@link ProvanceLedger}. */
    public static final class Builder {

        private ProvanceOptions options = new ProvanceOptions();
        private LedgerStore store;
        private LedgerSigner signer;
        private PayloadSerializer serializer = SimplePayloadSerializer.INSTANCE;
        private Clock clock = Clock.systemUTC();
        private Executor callbackExecutor = ForkJoinPool.commonPool();
        private final List<LedgerCheckpointSink> sinks = new ArrayList<>();
        private LedgerStore archive;

        private Builder() {
        }

        /** Uses a copy of {@code options} as the starting point. */
        public Builder options(ProvanceOptions options) {
            this.options = Objects.requireNonNull(options, "options").copy();
            return this;
        }

        /** Changes options in code. */
        public Builder configure(Consumer<ProvanceOptions> configure) {
            configure.accept(options);
            return this;
        }

        /** Required. Stable identifier of the ledger. */
        public Builder ledgerId(String ledgerId) {
            options.setLedgerId(ledgerId);
            return this;
        }

        /** HMAC secret of the current key (at least 32 bytes). Not needed when {@link #signer(LedgerSigner)} is used. */
        public Builder secretKey(String secretKey) {
            options.setSecretKey(secretKey);
            return this;
        }

        /** Identifier of the current HMAC key. Default {@code k1}. */
        public Builder signingKeyId(String signingKeyId) {
            options.setSigningKeyId(signingKeyId);
            return this;
        }

        /** Required. Where entries are stored. */
        public Builder store(LedgerStore store) {
            this.store = Objects.requireNonNull(store, "store");
            return this;
        }

        /**
         * Copies every entry to a second store, the archive, in the background. Give the application an account that
         * can only insert into it (and update the payload column, for erasure requests). See {@code docs/ARCHIVE.md}.
         */
        public Builder archive(LedgerStore archive) {
            this.archive = archive;
            return this;
        }

        /** Replaces the default HMAC signer, for example with a KMS backed implementation. */
        public Builder signer(LedgerSigner signer) {
            this.signer = Objects.requireNonNull(signer, "signer");
            return this;
        }

        /** Serializer used for payload objects. Default {@link SimplePayloadSerializer}. */
        public Builder payloadSerializer(PayloadSerializer serializer) {
            this.serializer = Objects.requireNonNull(serializer, "serializer");
            return this;
        }

        /** Adds a checkpoint sink. Checkpoints are published at the checkpoint interval when the head has moved. */
        public Builder checkpointSink(LedgerCheckpointSink sink) {
            sinks.add(Objects.requireNonNull(sink, "sink"));
            return this;
        }

        /** Adds a {@link FileCheckpointSink} writing under {@code directory}. */
        public Builder fileCheckpoints(Path directory) {
            return checkpointSink(new FileCheckpointSink(directory));
        }

        /** Clock used for timestamps. Default UTC system clock. */
        public Builder clock(Clock clock) {
            this.clock = Objects.requireNonNull(clock, "clock");
            return this;
        }

        /** Executor running the dependent stages of {@code addEntryAsync} futures. Default common fork-join pool. */
        public Builder callbackExecutor(Executor executor) {
            this.callbackExecutor = Objects.requireNonNull(executor, "executor");
            return this;
        }

        /**
         * Validates the configuration and starts the ledger.
         *
         * @throws IllegalStateException the configuration is invalid, no store is configured, or the HMAC key is missing,
         *                               too short or a placeholder
         */
        public ProvanceLedger build() {
            options.validate();
            if (store == null) {
                throw new IllegalStateException(
                        "No PROVANCE store is configured. Call store(...) with a JdbcLedgerStore, an InMemoryLedgerStore or your own LedgerStore.");
            }
            ProvanceOptions frozen = options.copy();
            Builder b = this;
            b.options = frozen;
            return new ProvanceLedger(b);
        }
    }
}
