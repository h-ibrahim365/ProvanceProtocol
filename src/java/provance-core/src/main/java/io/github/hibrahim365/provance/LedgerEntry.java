package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.json.Json;

import java.time.Instant;
import java.util.Objects;
import java.util.UUID;
import java.util.function.Function;

/**
 * A sealed, immutable entry of a PROVANCE ledger.
 *
 * <p>{@link #hash()} is the SHA-256 of the canonical envelope (every field except {@code hash},
 * {@code signature} and {@code payloadJson}). The payload is covered through {@link #payloadHash()},
 * which is why a payload can be redacted without breaking the chain. See {@code docs/SPEC.md}.
 *
 * @param formatVersion format version of the signed envelope, currently {@link ProvanceProtocol#FORMAT_VERSION}
 * @param ledgerId      identifier of the ledger this entry belongs to
 * @param sequence      position in the chain, starting at 1, without gaps
 * @param id            unique identifier of the entry
 * @param timestamp     UTC time at which the writer sealed the entry, truncated to the millisecond
 * @param eventType     application defined event type, for example {@code USER_LOGIN}
 * @param payloadJson   canonical JSON (RFC 8785) of the payload, or {@code null} when the payload was redacted
 * @param payloadHash   lowercase hex SHA-256 of the UTF-8 bytes of the canonical payload
 * @param previousHash  hash of the previous entry, or the genesis hash for the first entry
 * @param keyId         identifier of the key that produced the signature
 * @param hash          lowercase hex SHA-256 of the canonical envelope
 * @param signature     lowercase hex signature of the 32 raw bytes of the hash
 */
public record LedgerEntry(
        int formatVersion,
        String ledgerId,
        long sequence,
        UUID id,
        Instant timestamp,
        String eventType,
        String payloadJson,
        String payloadHash,
        String previousHash,
        String keyId,
        String hash,
        String signature) {

    /** Validates that every field except {@code payloadJson} is present. */
    public LedgerEntry {
        Objects.requireNonNull(ledgerId, "ledgerId");
        Objects.requireNonNull(id, "id");
        Objects.requireNonNull(timestamp, "timestamp");
        Objects.requireNonNull(eventType, "eventType");
        Objects.requireNonNull(payloadHash, "payloadHash");
        Objects.requireNonNull(previousHash, "previousHash");
        Objects.requireNonNull(keyId, "keyId");
        Objects.requireNonNull(hash, "hash");
        Objects.requireNonNull(signature, "signature");
    }

    /** {@code true} when the payload has been removed (for example after a GDPR erasure request). */
    public boolean isRedacted() {
        return payloadJson == null;
    }

    /**
     * Parses the payload into plain Java values (see {@link Json#parse(String)}), or returns {@code null}
     * when the payload was redacted.
     */
    public Object readPayload() {
        return payloadJson == null ? null : Json.parse(payloadJson);
    }

    /**
     * Converts the payload with your own JSON library, for example
     * {@code entry.readPayload(json -> mapper.readValue(json, Order.class))}. Returns {@code null} when redacted.
     */
    public <T> T readPayload(Function<String, T> deserializer) {
        return payloadJson == null ? null : deserializer.apply(payloadJson);
    }

    /** Copy of this entry with another payload (used by stores to redact). */
    public LedgerEntry withPayloadJson(String newPayloadJson) {
        return new LedgerEntry(formatVersion, ledgerId, sequence, id, timestamp, eventType, newPayloadJson,
                payloadHash, previousHash, keyId, hash, signature);
    }

    LedgerEntry withHashAndSignature(String newHash, String newSignature) {
        return new LedgerEntry(formatVersion, ledgerId, sequence, id, timestamp, eventType, payloadJson,
                payloadHash, previousHash, keyId, newHash, newSignature);
    }
}
