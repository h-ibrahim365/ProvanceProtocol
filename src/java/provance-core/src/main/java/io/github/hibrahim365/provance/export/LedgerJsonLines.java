package io.github.hibrahim365.provance.export;

import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.Json;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.OffsetDateTime;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.stream.Stream;

/**
 * Ledger export as JSON Lines: one entry per line, with the field names {@code v}, {@code ledgerId}, {@code sequence},
 * {@code id}, {@code timestamp}, {@code eventType}, {@code payloadJson}, {@code payloadHash}, {@code previousHash},
 * {@code keyId}, {@code hash} and {@code signature}. Same format as the .NET and Python libraries.
 */
public final class LedgerJsonLines {

    private LedgerJsonLines() {
    }

    /** Serializes one entry to a single line (without the line break). */
    public static String serialize(LedgerEntry e) {
        StringBuilder sb = new StringBuilder(512).append('{');
        sb.append("\"v\":").append(e.formatVersion());
        field(sb, "ledgerId", e.ledgerId());
        sb.append(",\"sequence\":").append(e.sequence());
        field(sb, "id", e.id().toString());
        field(sb, "timestamp", LedgerHashing.formatTimestamp(e.timestamp()));
        field(sb, "eventType", e.eventType());
        field(sb, "payloadJson", e.payloadJson());
        field(sb, "payloadHash", e.payloadHash());
        field(sb, "previousHash", e.previousHash());
        field(sb, "keyId", e.keyId());
        field(sb, "hash", e.hash());
        field(sb, "signature", e.signature());
        // U+2028 and U+2029 are escaped so that every tool sees one entry per line.
        return sb.append('}').toString().replace("\u2028", "\\u2028").replace("\u2029", "\\u2029");
    }

    /** Parses one line written by any PROVANCE implementation. */
    public static LedgerEntry parse(String line) {
        try {
            Map<String, Object> o = Json.parseObject(line);
            return new LedgerEntry(((Number) o.get("v")).intValue(), (String) o.get("ledgerId"),
                    ((Number) o.get("sequence")).longValue(), UUID.fromString((String) o.get("id")),
                    OffsetDateTime.parse((String) o.get("timestamp")).toInstant(), (String) o.get("eventType"),
                    (String) o.get("payloadJson"), (String) o.get("payloadHash"), (String) o.get("previousHash"),
                    (String) o.get("keyId"), (String) o.get("hash"), (String) o.get("signature"));
        } catch (RuntimeException e) {
            throw new IllegalArgumentException("Not a PROVANCE ledger entry: " + e.getMessage(), e);
        }
    }

    /** Writes every entry of a store, one per line; returns how many were written. */
    public static long write(LedgerStore store, Writer out) throws IOException {
        long count = 0;
        try (Stream<LedgerEntry> entries = store.read(1)) {
            for (LedgerEntry e : (Iterable<LedgerEntry>) entries::iterator) {
                out.write(serialize(e));
                out.write('\n');
                count++;
            }
        }
        out.flush();
        return count;
    }

    /** Loads an export file as a read-only store, in file order, to verify it or compare it with another copy. */
    public static JsonLinesLedgerFile open(Path path) throws IOException {
        List<LedgerEntry> entries = new ArrayList<>();
        try (BufferedReader reader = Files.newBufferedReader(path, StandardCharsets.UTF_8)) {
            String line;
            int number = 0;
            while ((line = readLine(reader)) != null) {
                number++;
                if (line.isBlank()) {
                    continue;
                }
                try {
                    entries.add(parse(line));
                } catch (IllegalArgumentException e) {
                    throw new IllegalArgumentException("Line " + number + ": " + e.getMessage(), e);
                }
            }
        }
        return new JsonLinesLedgerFile(entries);
    }

    /** Reads up to '\n' only: BufferedReader.readLine would also split on a lone '\r'. */
    private static String readLine(BufferedReader reader) throws IOException {
        StringBuilder sb = new StringBuilder();
        int c;
        while ((c = reader.read()) != -1) {
            if (c == '\n') {
                return sb.length() > 0 && sb.charAt(sb.length() - 1) == '\r' ? sb.substring(0, sb.length() - 1) : sb.toString();
            }
            sb.append((char) c);
        }
        return sb.length() == 0 ? null : sb.toString();
    }

    private static void field(StringBuilder sb, String name, String value) {
        sb.append(",\"").append(name).append("\":");
        if (value == null) {
            sb.append("null");
        } else {
            JsonCanonicalizer.writeString(value, sb);
        }
    }
}
