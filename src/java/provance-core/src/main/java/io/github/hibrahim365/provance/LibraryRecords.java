package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.Json;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;

import java.util.Map;
import java.util.Optional;
import java.util.UUID;

/**
 * Entries the library writes itself ({@code docs/SPEC.md}, sections 6 and 9). Event types starting with
 * {@code provance.} are reserved for them: applications cannot write such entries.
 */
public final class LibraryRecords {

    /** Prefix reserved for entries written by the library. */
    public static final String RESERVED_PREFIX = "provance.";
    /** Event type of the record appended before old entries are removed (retention). */
    public static final String RETENTION_EVENT = "provance.retention";
    /** Event type of the record appended before a payload is erased. */
    public static final String REDACTION_EVENT = "provance.redaction";

    /** First entry kept by retention, named by a retention record. */
    public record RetainedStart(long firstSequence, String firstHash) {
    }

    /** Entry named by a redaction record. */
    public record ErasedEntry(long sequence, UUID entryId) {
    }

    private LibraryRecords() {
    }

    /** {@code true} for event types starting with {@code provance.}, in any case. */
    public static boolean isReserved(String eventType) {
        return eventType != null && eventType.regionMatches(true, 0, RESERVED_PREFIX, 0, RESERVED_PREFIX.length());
    }

    /** Canonical payload of a retention record. */
    public static String retentionPayload(long firstSequence, String firstHash) {
        if (firstSequence < 2) {
            throw new IllegalArgumentException("Retention keeps entries from sequence 2 or later.");
        }
        if (!LedgerHashing.isLowerHex(firstHash, 64)) {
            throw new IllegalArgumentException("Hash must be 64 lowercase hex characters.");
        }
        return JsonCanonicalizer.canonicalize("{\"firstHash\":\"" + firstHash + "\",\"firstSequence\":" + firstSequence + "}");
    }

    /** Reads a retention record's payload; empty when missing or malformed. */
    public static Optional<RetainedStart> parseRetention(String payloadJson) {
        Map<String, Object> data = object(payloadJson);
        if (data == null || !(data.get("firstSequence") instanceof Number n) || !(data.get("firstHash") instanceof String hash)) {
            return Optional.empty();
        }
        long sequence = n.longValue();
        if (sequence < 2 || n.doubleValue() != sequence || !LedgerHashing.isLowerHex(hash, 64)) {
            return Optional.empty();
        }
        return Optional.of(new RetainedStart(sequence, hash));
    }

    /** Canonical payload of a redaction record. */
    public static String redactionPayload(long sequence, UUID entryId) {
        if (sequence < 1) {
            throw new IllegalArgumentException("Sequence must be positive.");
        }
        return JsonCanonicalizer.canonicalize("{\"entryId\":\"" + entryId + "\",\"sequence\":" + sequence + "}");
    }

    /** Reads a redaction record's payload; empty when missing or malformed. */
    public static Optional<ErasedEntry> parseRedaction(String payloadJson) {
        Map<String, Object> data = object(payloadJson);
        if (data == null || !(data.get("sequence") instanceof Number n) || !(data.get("entryId") instanceof String id)) {
            return Optional.empty();
        }
        long sequence = n.longValue();
        if (sequence < 1 || n.doubleValue() != sequence) {
            return Optional.empty();
        }
        try {
            return Optional.of(new ErasedEntry(sequence, UUID.fromString(id)));
        } catch (IllegalArgumentException e) {
            return Optional.empty();
        }
    }

    private static Map<String, Object> object(String payloadJson) {
        if (payloadJson == null) {
            return null;
        }
        try {
            return Json.parseObject(payloadJson);
        } catch (RuntimeException e) {
            return null;
        }
    }
}
