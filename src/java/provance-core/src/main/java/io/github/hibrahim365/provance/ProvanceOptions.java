package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;

import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Pattern;

/**
 * Configuration of a PROVANCE ledger. Same settings, names and defaults as the .NET library.
 */
public class ProvanceOptions {

    private static final Pattern LEDGER_ID = Pattern.compile("^[A-Za-z0-9._:-]{1,128}$");

    private String ledgerId = "";
    private String genesisHash;
    private String signingKeyId = "k1";
    private String secretKey;
    private Map<String, String> retiredKeys = new LinkedHashMap<>();
    private int queueCapacity = 10_000;
    private QueueFullBehavior queueFullBehavior = QueueFullBehavior.WAIT;
    private int maxBatchSize = 256;
    private int maxAppendAttempts = 5;
    private Duration retryBaseDelay = Duration.ofMillis(50);
    private int maxConflictRetries = 100;
    private Duration checkpointInterval = Duration.ofMinutes(5);
    private Duration shutdownTimeout = Duration.ofSeconds(30);

    /**
     * Required. Stable identifier of the ledger, for example {@code orders-api-prod}.
     * Allowed characters: letters, digits, {@code . _ : -}, up to 128 characters.
     */
    public String getLedgerId() {
        return ledgerId;
    }

    /** @see #getLedgerId() */
    public void setLedgerId(String ledgerId) {
        this.ledgerId = ledgerId;
    }

    /**
     * Optional. Hash used as {@code previousHash} of the first entry (64 lowercase hex characters).
     * Defaults to {@link LedgerHashing#deriveGenesisHash(String)} of the ledger id.
     */
    public String getGenesisHash() {
        return genesisHash;
    }

    /** @see #getGenesisHash() */
    public void setGenesisHash(String genesisHash) {
        this.genesisHash = genesisHash;
    }

    /** Identifier of the current signing key. Change it whenever you rotate the key. Default {@code k1}. */
    public String getSigningKeyId() {
        return signingKeyId;
    }

    /** @see #getSigningKeyId() */
    public void setSigningKeyId(String signingKeyId) {
        this.signingKeyId = signingKeyId;
    }

    /**
     * HMAC secret of the current key, at least 32 bytes. Load it from a secret store or an environment variable,
     * never from a file in source control. Not used when you provide your own signer.
     */
    public String getSecretKey() {
        return secretKey;
    }

    /** @see #getSecretKey() */
    public void setSecretKey(String secretKey) {
        this.secretKey = secretKey;
    }

    /** Retired keys (key id to secret), used only to verify old entries after a rotation. */
    public Map<String, String> getRetiredKeys() {
        return retiredKeys;
    }

    /** @see #getRetiredKeys() */
    public void setRetiredKeys(Map<String, String> retiredKeys) {
        this.retiredKeys = retiredKeys == null ? new LinkedHashMap<>() : retiredKeys;
    }

    /** Maximum number of entries waiting for the writer. Default 10 000. */
    public int getQueueCapacity() {
        return queueCapacity;
    }

    /** @see #getQueueCapacity() */
    public void setQueueCapacity(int queueCapacity) {
        this.queueCapacity = queueCapacity;
    }

    /** Behavior when the queue is full. Default {@link QueueFullBehavior#WAIT}. */
    public QueueFullBehavior getQueueFullBehavior() {
        return queueFullBehavior;
    }

    /** @see #getQueueFullBehavior() */
    public void setQueueFullBehavior(QueueFullBehavior queueFullBehavior) {
        this.queueFullBehavior = queueFullBehavior;
    }

    /** Maximum number of entries appended to the store in one call. Default 256. */
    public int getMaxBatchSize() {
        return maxBatchSize;
    }

    /** @see #getMaxBatchSize() */
    public void setMaxBatchSize(int maxBatchSize) {
        this.maxBatchSize = maxBatchSize;
    }

    /** Attempts for a batch that fails with a transient store error before callers get an exception. Default 5. */
    public int getMaxAppendAttempts() {
        return maxAppendAttempts;
    }

    /** @see #getMaxAppendAttempts() */
    public void setMaxAppendAttempts(int maxAppendAttempts) {
        this.maxAppendAttempts = maxAppendAttempts;
    }

    /** Base delay of the exponential backoff between transient failures. Default 50 ms, capped at 2 s. */
    public Duration getRetryBaseDelay() {
        return retryBaseDelay;
    }

    /** @see #getRetryBaseDelay() */
    public void setRetryBaseDelay(Duration retryBaseDelay) {
        this.retryBaseDelay = retryBaseDelay;
    }

    /**
     * Attempts when another writer appended first (sequence conflict). Conflicts are resolved by reloading the head
     * and resealing, without delay. Default 100.
     */
    public int getMaxConflictRetries() {
        return maxConflictRetries;
    }

    /** @see #getMaxConflictRetries() */
    public void setMaxConflictRetries(int maxConflictRetries) {
        this.maxConflictRetries = maxConflictRetries;
    }

    /** Interval between automatic checkpoints. Only used when a checkpoint sink is configured. Default 5 minutes. */
    public Duration getCheckpointInterval() {
        return checkpointInterval;
    }

    /** @see #getCheckpointInterval() */
    public void setCheckpointInterval(Duration checkpointInterval) {
        this.checkpointInterval = checkpointInterval;
    }

    /** How long {@code close()} waits for queued entries to be written. Default 30 seconds. */
    public Duration getShutdownTimeout() {
        return shutdownTimeout;
    }

    /** @see #getShutdownTimeout() */
    public void setShutdownTimeout(Duration shutdownTimeout) {
        this.shutdownTimeout = shutdownTimeout;
    }

    /** The genesis hash actually used: {@link #getGenesisHash()} if set, otherwise derived from the ledger id. */
    public String effectiveGenesisHash() {
        return genesisHash == null || genesisHash.isEmpty() ? LedgerHashing.deriveGenesisHash(ledgerId) : genesisHash;
    }

    /** Copies every setting (the retired keys map is copied too). */
    public ProvanceOptions copy() {
        ProvanceOptions c = new ProvanceOptions();
        c.ledgerId = ledgerId;
        c.genesisHash = genesisHash;
        c.signingKeyId = signingKeyId;
        c.secretKey = secretKey;
        c.retiredKeys = new LinkedHashMap<>(retiredKeys);
        c.queueCapacity = queueCapacity;
        c.queueFullBehavior = queueFullBehavior;
        c.maxBatchSize = maxBatchSize;
        c.maxAppendAttempts = maxAppendAttempts;
        c.retryBaseDelay = retryBaseDelay;
        c.maxConflictRetries = maxConflictRetries;
        c.checkpointInterval = checkpointInterval;
        c.shutdownTimeout = shutdownTimeout;
        return c;
    }

    /**
     * Checks every setting and reports all problems at once.
     *
     * @throws IllegalStateException the configuration is invalid
     */
    public void validate() {
        List<String> errors = new ArrayList<>();

        if (ledgerId == null || ledgerId.isBlank()) {
            errors.add("ledgerId is required (for example \"orders-api-prod\").");
        } else if (!LEDGER_ID.matcher(ledgerId).matches()) {
            errors.add("ledgerId may only contain letters, digits and . _ : - (max 128 characters).");
        }
        if (genesisHash != null && !genesisHash.isEmpty() && !LedgerHashing.isLowerHex(genesisHash, 64)) {
            errors.add("genesisHash must be 64 lowercase hex characters.");
        }
        if (signingKeyId == null || signingKeyId.isBlank()) {
            errors.add("signingKeyId is required.");
        }
        if (queueCapacity < 1) {
            errors.add("queueCapacity must be at least 1.");
        }
        if (queueFullBehavior == null) {
            errors.add("queueFullBehavior is required.");
        }
        if (maxBatchSize < 1 || maxBatchSize > 10_000) {
            errors.add("maxBatchSize must be between 1 and 10000.");
        }
        if (maxAppendAttempts < 1) {
            errors.add("maxAppendAttempts must be at least 1.");
        }
        if (maxConflictRetries < 0) {
            errors.add("maxConflictRetries cannot be negative.");
        }
        if (retryBaseDelay == null || retryBaseDelay.isNegative()) {
            errors.add("retryBaseDelay cannot be negative.");
        }
        if (checkpointInterval == null || checkpointInterval.isZero() || checkpointInterval.isNegative()) {
            errors.add("checkpointInterval must be positive.");
        }
        if (shutdownTimeout == null || shutdownTimeout.isNegative()) {
            errors.add("shutdownTimeout cannot be negative.");
        }

        if (!errors.isEmpty()) {
            throw new IllegalStateException("PROVANCE configuration is invalid: " + String.join(" ", errors));
        }
    }
}
