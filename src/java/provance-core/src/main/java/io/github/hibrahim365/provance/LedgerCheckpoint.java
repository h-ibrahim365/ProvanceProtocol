package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.Json;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;
import io.github.hibrahim365.provance.json.JsonFormatException;

import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.format.DateTimeParseException;
import java.util.Map;
import java.util.Objects;

/**
 * A signed statement "the ledger head was at {@code sequence} with {@code hash}".
 *
 * <p>Checkpoints are what makes truncation and full rewrites detectable: publish them somewhere the
 * application cannot modify (object storage with retention lock, WORM storage, a timestamping
 * authority, another team's system) and give the latest one to the verifier.
 *
 * <p>The JSON form ({@link #toJson()}) is the same as the one produced by the .NET library, so a checkpoint
 * written by one implementation can be read by the other.
 *
 * @param formatVersion format version of the checkpoint
 * @param ledgerId      ledger the checkpoint refers to
 * @param sequence      sequence of the head entry at checkpoint time
 * @param hash          hash of the head entry at checkpoint time
 * @param createdAt     UTC creation time, truncated to the millisecond
 * @param keyId         key used to sign the checkpoint
 * @param signature     lowercase hex signature of the SHA-256 of the canonical checkpoint body
 */
public record LedgerCheckpoint(
        int formatVersion,
        String ledgerId,
        long sequence,
        String hash,
        Instant createdAt,
        String keyId,
        String signature) {

    /** Validates that every field is present. */
    public LedgerCheckpoint {
        Objects.requireNonNull(ledgerId, "ledgerId");
        Objects.requireNonNull(hash, "hash");
        Objects.requireNonNull(createdAt, "createdAt");
        Objects.requireNonNull(keyId, "keyId");
        Objects.requireNonNull(signature, "signature");
    }

    /** Serializes the checkpoint to JSON (camelCase, {@code v} for the format version). */
    public String toJson() {
        StringBuilder sb = new StringBuilder(320);
        sb.append("{\"v\":").append(formatVersion);
        sb.append(",\"ledgerId\":");
        appendString(sb, ledgerId);
        sb.append(",\"sequence\":").append(sequence);
        sb.append(",\"hash\":");
        appendString(sb, hash);
        sb.append(",\"createdAt\":");
        appendString(sb, LedgerHashing.formatTimestamp(createdAt));
        sb.append(",\"keyId\":");
        appendString(sb, keyId);
        sb.append(",\"signature\":");
        appendString(sb, signature);
        return sb.append('}').toString();
    }

    /**
     * Parses a checkpoint produced by {@link #toJson()} or by the .NET library.
     *
     * @throws JsonFormatException the JSON is invalid or a field is missing
     */
    public static LedgerCheckpoint fromJson(String json) {
        Map<String, Object> o = Json.parseObject(json);
        try {
            return new LedgerCheckpoint(
                    ((Number) required(o, "v")).intValue(),
                    (String) required(o, "ledgerId"),
                    ((Number) required(o, "sequence")).longValue(),
                    (String) required(o, "hash"),
                    OffsetDateTime.parse((String) required(o, "createdAt")).toInstant(),
                    (String) required(o, "keyId"),
                    (String) required(o, "signature"));
        } catch (ClassCastException | DateTimeParseException e) {
            throw new JsonFormatException("Checkpoint JSON has a field of the wrong type: " + e.getMessage(), e);
        }
    }

    LedgerCheckpoint withSignature(String newSignature) {
        return new LedgerCheckpoint(formatVersion, ledgerId, sequence, hash, createdAt, keyId, newSignature);
    }

    private static Object required(Map<String, Object> o, String name) {
        Object value = o.get(name);
        if (value == null) {
            throw new JsonFormatException("Checkpoint JSON has no '" + name + "' field.");
        }
        return value;
    }

    private static void appendString(StringBuilder sb, String value) {
        JsonCanonicalizer.writeString(value, sb);
    }
}
