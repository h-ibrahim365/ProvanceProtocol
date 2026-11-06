package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.checkpoint.FileCheckpointSink;
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
        this.writer = new LedgerWriter(store, signer, options, clock);
        this.writerThread = new Thread(writer, "provance-writer-" + options.getLedgerId());
        this.writerThread.setDaemon(true);
        this.checkpoints = new CheckpointPublisher(this, b.sinks, options.getCheckpointInterval());
        this.writerThread.start();
        this.checkpoints.start(options.getLedgerId());
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

    @Override
    public boolean redactPayload(UUID id) {
        return store.redactPayload(id);
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
    }

    private PendingEntry prepare(String eventType, Object payload) {
        if (eventType == null || eventType.isBlank()) {
            throw new IllegalArgumentException("Event type is required.");
        }
        if (eventType.length() > MAX_EVENT_TYPE_LENGTH) {
            throw new IllegalArgumentException("Event type is limited to " + MAX_EVENT_TYPE_LENGTH + " characters.");
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
