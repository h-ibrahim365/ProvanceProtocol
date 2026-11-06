package io.github.hibrahim365.provance.crypto;

import io.github.hibrahim365.provance.LedgerCheckpoint;
import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.ProvanceProtocol;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.time.temporal.ChronoUnit;
import java.util.HexFormat;
import java.util.Objects;

/**
 * Builds the exact bytes that PROVANCE hashes and signs. Public so that auditors and other implementations
 * can reproduce the computation (see {@code docs/SPEC.md} and {@code docs/test-vectors}).
 */
public final class LedgerHashing {

    /** Timestamp format used in canonical bodies: UTC, millisecond precision, {@code Z} suffix. */
    public static final String TIMESTAMP_PATTERN = "uuuu-MM-dd'T'HH:mm:ss.SSS'Z'";

    private static final DateTimeFormatter TIMESTAMP_FORMAT =
            DateTimeFormatter.ofPattern(TIMESTAMP_PATTERN).withZone(ZoneOffset.UTC);

    private static final HexFormat HEX = HexFormat.of();

    private LedgerHashing() {
    }

    /** Lowercase hex SHA-256 of the UTF-8 bytes of {@code text}. */
    public static String sha256Hex(String text) {
        Objects.requireNonNull(text, "text");
        return HEX.formatHex(sha256(text.getBytes(StandardCharsets.UTF_8)));
    }

    /** SHA-256 of {@code data}. */
    public static byte[] sha256(byte[] data) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(data);
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is not available in this JVM.", e);
        }
    }

    /** Default genesis hash of a ledger: SHA-256 of {@code "provance:genesis:v1:" + ledgerId}. */
    public static String deriveGenesisHash(String ledgerId) {
        if (ledgerId == null || ledgerId.isBlank()) {
            throw new IllegalArgumentException("ledgerId is required");
        }
        return sha256Hex(ProvanceProtocol.GENESIS_PREFIX + ledgerId);
    }

    /** Truncates a timestamp to the millisecond. */
    public static Instant normalizeTimestamp(Instant timestamp) {
        return timestamp.truncatedTo(ChronoUnit.MILLIS);
    }

    /** Formats a timestamp the way it appears in canonical bodies, for example {@code 2026-01-15T09:30:00.123Z}. */
    public static String formatTimestamp(Instant timestamp) {
        return TIMESTAMP_FORMAT.format(normalizeTimestamp(timestamp));
    }

    /**
     * Canonical envelope of an entry: every field except {@code hash}, {@code signature} and the payload itself
     * (which is covered by {@code payloadHash}).
     */
    public static String canonicalEnvelope(LedgerEntry entry) {
        Objects.requireNonNull(entry, "entry");
        if (entry.sequence() < 1 || entry.sequence() > ProvanceProtocol.MAX_SEQUENCE) {
            throw new IllegalArgumentException("Sequence must be between 1 and " + ProvanceProtocol.MAX_SEQUENCE + ".");
        }
        StringBuilder sb = new StringBuilder(512);
        sb.append('{');
        member(sb, "eventType", entry.eventType()).append(',');
        member(sb, "id", entry.id().toString()).append(',');
        member(sb, "keyId", entry.keyId()).append(',');
        member(sb, "ledgerId", entry.ledgerId()).append(',');
        member(sb, "payloadHash", entry.payloadHash()).append(',');
        member(sb, "previousHash", entry.previousHash()).append(',');
        sb.append("\"sequence\":").append(entry.sequence()).append(',');
        member(sb, "timestamp", formatTimestamp(entry.timestamp())).append(',');
        sb.append("\"v\":").append(entry.formatVersion());
        sb.append('}');
        // Members are already sorted; canonicalize() normalizes numbers and guarantees the RFC 8785 form.
        return JsonCanonicalizer.canonicalize(sb.toString());
    }

    /** Lowercase hex SHA-256 of the canonical envelope. */
    public static String computeEntryHash(LedgerEntry entry) {
        return sha256Hex(canonicalEnvelope(entry));
    }

    /** Canonical body of a checkpoint (every field except {@code signature}). */
    public static String canonicalCheckpoint(LedgerCheckpoint checkpoint) {
        Objects.requireNonNull(checkpoint, "checkpoint");
        StringBuilder sb = new StringBuilder(320);
        sb.append('{');
        member(sb, "createdAt", formatTimestamp(checkpoint.createdAt())).append(',');
        member(sb, "hash", checkpoint.hash()).append(',');
        member(sb, "keyId", checkpoint.keyId()).append(',');
        member(sb, "ledgerId", checkpoint.ledgerId()).append(',');
        sb.append("\"sequence\":").append(checkpoint.sequence()).append(',');
        member(sb, "type", "checkpoint").append(',');
        sb.append("\"v\":").append(checkpoint.formatVersion());
        sb.append('}');
        return JsonCanonicalizer.canonicalize(sb.toString());
    }

    /** Lowercase hex SHA-256 of the canonical checkpoint body. */
    public static String computeCheckpointHash(LedgerCheckpoint checkpoint) {
        return sha256Hex(canonicalCheckpoint(checkpoint));
    }

    /**
     * Decodes a 64 character lowercase hex hash into its 32 bytes.
     *
     * @throws IllegalArgumentException the value is not 64 lowercase hex characters
     */
    public static byte[] hashToBytes(String hexHash) {
        if (!isLowerHex(hexHash, 64)) {
            throw new IllegalArgumentException("Hash must be 64 lowercase hex characters.");
        }
        return HEX.parseHex(hexHash);
    }

    /** Lowercase hex encoding. */
    public static String toHex(byte[] bytes) {
        return HEX.formatHex(bytes);
    }

    /** Decodes lowercase hex of any even length. */
    public static byte[] fromHex(String hex) {
        if (!isLowerHex(hex)) {
            throw new IllegalArgumentException("Value is not lowercase hex.");
        }
        return HEX.parseHex(hex);
    }

    /** {@code true} if {@code value} is non empty lowercase hex with an even length. */
    public static boolean isLowerHex(String value) {
        return isLowerHex(value, -1);
    }

    /** {@code true} if {@code value} is lowercase hex of exactly {@code length} characters ({@code -1}: any even length). */
    public static boolean isLowerHex(String value, int length) {
        if (value == null || value.isEmpty() || value.length() % 2 != 0) return false;
        if (length >= 0 && value.length() != length) return false;
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    private static StringBuilder member(StringBuilder sb, String name, String value) {
        sb.append('"').append(name).append("\":");
        JsonCanonicalizer.writeString(value, sb);
        return sb;
    }
}
