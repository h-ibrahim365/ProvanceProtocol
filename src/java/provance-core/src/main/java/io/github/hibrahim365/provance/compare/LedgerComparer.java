package io.github.hibrahim365.provance.compare;

import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.LibraryRecords;
import io.github.hibrahim365.provance.ProvanceProtocol;
import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.Json;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;
import io.github.hibrahim365.provance.signing.LedgerSigner;
import io.github.hibrahim365.provance.signing.SignatureCheck;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.stream.Stream;

/**
 * Compares the primary store of a ledger with a second copy (the archive, or an export) and lists, field by field,
 * what differs. The copy whose entry still verifies on its own is the original. Same algorithm and report as the
 * .NET and Python libraries.
 */
public final class LedgerComparer {

    private final LedgerSigner signer;
    private final String ledgerId;

    /** The signer must know every key used in the ledger, retired ones included. */
    public LedgerComparer(LedgerSigner signer, String ledgerId) {
        this.signer = Objects.requireNonNull(signer, "signer");
        this.ledgerId = Objects.requireNonNull(ledgerId, "ledgerId");
    }

    /** Compares two copies, reading both in sequence order at the same time. */
    public LedgerComparison compare(LedgerStore primary, LedgerStore archive, int maxDifferences) {
        if (maxDifferences < 1) {
            throw new IllegalArgumentException("maxDifferences must be at least 1.");
        }
        State s = new State(maxDifferences);
        try (Stream<LedgerEntry> ps = primary.read(1); Stream<LedgerEntry> as = archive.read(1)) {
            Iterator<LedgerEntry> p = ps.iterator();
            Iterator<LedgerEntry> a = as.iterator();
            LedgerEntry pe = p.hasNext() ? p.next() : null;
            LedgerEntry ae = a.hasNext() ? a.next() : null;
            long lastP = 0, lastA = 0;
            while (pe != null || ae != null) {
                if (pe != null && pe.sequence() <= lastP) {
                    s.add(new EntryDifference(pe.sequence(), null, DifferenceKind.MISSING_FROM_ARCHIVE, null, pe.eventType(),
                            List.of(new FieldDifference("sequence", null, pe.sequence() + " (out of order, after " + lastP + ")"))));
                    pe = p.hasNext() ? p.next() : null;
                    continue;
                }
                if (ae != null && ae.sequence() <= lastA) {
                    s.add(new EntryDifference(ae.sequence(), null, DifferenceKind.MISSING_FROM_PRIMARY, null, ae.eventType(),
                            List.of(new FieldDifference("sequence", ae.sequence() + " (out of order, after " + lastA + ")", null))));
                    s.archiveLast = Math.max(s.archiveLast, ae.sequence());
                    ae = a.hasNext() ? a.next() : null;
                    continue;
                }
                if (pe != null && s.primaryFirst == 0) {
                    s.primaryFirst = pe.sequence();
                    s.primaryFirstHash = pe.hash();
                }
                if (pe != null && ae != null && pe.sequence() == ae.sequence()) {
                    s.compared++;
                    s.primaryLast = pe.sequence();
                    s.archiveLast = ae.sequence();
                    s.checkRecord(pe);
                    compareEntry(ae, pe).ifPresent(s::add);
                    lastP = pe.sequence();
                    lastA = ae.sequence();
                    pe = p.hasNext() ? p.next() : null;
                    ae = a.hasNext() ? a.next() : null;
                } else if (pe != null && (ae == null || pe.sequence() < ae.sequence())) {
                    s.primaryLast = pe.sequence();
                    s.checkRecord(pe);
                    if (ae == null) {
                        s.notYetCopied++; // the archive ends before this entry
                    } else {
                        s.add(EntryDifference.of(pe.sequence(), DifferenceKind.MISSING_FROM_ARCHIVE, pe.eventType()));
                    }
                    lastP = pe.sequence();
                    pe = p.hasNext() ? p.next() : null;
                } else {
                    s.archiveLast = ae.sequence();
                    if (lastP == 0) {
                        s.onlyInArchiveAtStart++; // the primary store starts later, or is empty: decided at the end
                    } else {
                        s.add(EntryDifference.of(ae.sequence(), DifferenceKind.MISSING_FROM_PRIMARY, ae.eventType()));
                    }
                    lastA = ae.sequence();
                    ae = a.hasNext() ? a.next() : null;
                }
            }
        }

        long removedByRetention = 0;
        if (s.onlyInArchiveAtStart > 0) {
            if (s.startVouched) {
                removedByRetention = s.onlyInArchiveAtStart;
            } else {
                long through = s.primaryFirst > 0 ? s.primaryFirst - 1 : s.archiveLast;
                s.differences.add(0, new EntryDifference(1, through, DifferenceKind.MISSING_FROM_PRIMARY, null, null, List.of()));
                if (s.differences.size() > maxDifferences) {
                    s.differences.remove(s.differences.size() - 1);
                    s.truncated = true;
                }
            }
        }
        return new LedgerComparison(s.differences, s.truncated, s.compared, removedByRetention, s.notYetCopied,
                s.primaryLast, s.archiveLast);
    }

    /** Compares two copies of the entry at one sequence; empty when identical. */
    public Optional<EntryDifference> compareEntry(LedgerEntry archive, LedgerEntry primary) {
        List<FieldDifference> fields = new ArrayList<>();
        field(fields, "id", archive.id().toString(), primary.id().toString());
        field(fields, "eventType", archive.eventType(), primary.eventType());
        field(fields, "timestamp", LedgerHashing.formatTimestamp(archive.timestamp()), LedgerHashing.formatTimestamp(primary.timestamp()));
        field(fields, "ledgerId", archive.ledgerId(), primary.ledgerId());
        field(fields, "v", String.valueOf(archive.formatVersion()), String.valueOf(primary.formatVersion()));
        field(fields, "keyId", archive.keyId(), primary.keyId());

        boolean redactedInOne = (archive.payloadJson() == null) != (primary.payloadJson() == null);
        if (archive.payloadJson() != null && primary.payloadJson() != null) {
            comparePayloads(archive.payloadJson(), primary.payloadJson(), fields);
        } else if (redactedInOne && !Objects.equals(archive.payloadHash(), primary.payloadHash())) {
            field(fields, "payload", archive.payloadJson(), primary.payloadJson());
        }
        field(fields, "payloadHash", archive.payloadHash(), primary.payloadHash());
        field(fields, "previousHash", archive.previousHash(), primary.previousHash());
        field(fields, "hash", archive.hash(), primary.hash());
        field(fields, "signature", archive.signature(), primary.signature());

        if (fields.isEmpty()) {
            return redactedInOne
                    ? Optional.of(EntryDifference.of(primary.sequence(), DifferenceKind.REDACTED_IN_ONE_COPY, primary.eventType()))
                    : Optional.empty();
        }
        boolean archiveOk = verifiesAlone(archive);
        boolean primaryOk = verifiesAlone(primary);
        OriginalCopy original = archiveOk && !primaryOk ? OriginalCopy.ARCHIVE
                : !archiveOk && primaryOk ? OriginalCopy.PRIMARY
                : archiveOk ? OriginalCopy.BOTH_VERIFY : OriginalCopy.NEITHER_VERIFIES;
        return Optional.of(new EntryDifference(primary.sequence(), null, DifferenceKind.CHANGED, original,
                original == OriginalCopy.PRIMARY ? primary.eventType() : archive.eventType(), fields));
    }

    /** Checks one entry without its neighbors: payload hash, envelope hash and signature. */
    public boolean verifiesAlone(LedgerEntry entry) {
        if (entry.formatVersion() != ProvanceProtocol.FORMAT_VERSION || !ledgerId.equals(entry.ledgerId())) {
            return false;
        }
        if (entry.payloadJson() != null && !LedgerHashing.sha256Hex(entry.payloadJson()).equals(entry.payloadHash())) {
            return false;
        }
        String hash;
        try {
            hash = LedgerHashing.computeEntryHash(entry);
        } catch (IllegalArgumentException e) {
            return false;
        }
        if (!hash.equals(entry.hash()) || !LedgerHashing.isLowerHex(entry.signature())) {
            return false;
        }
        return signer.verify(entry.keyId(), LedgerHashing.hashToBytes(hash), LedgerHashing.fromHex(entry.signature())) == SignatureCheck.VALID;
    }

    private static void field(List<FieldDifference> fields, String name, String before, String after) {
        if (!Objects.equals(before, after)) {
            fields.add(new FieldDifference(name, before, after));
        }
    }

    private static void comparePayloads(String archiveJson, String primaryJson, List<FieldDifference> fields) {
        if (archiveJson.equals(primaryJson)) {
            return;
        }
        Map<String, String> before = flatten(archiveJson);
        Map<String, String> after = flatten(primaryJson);
        if (before == null || after == null) {
            fields.add(new FieldDifference("payload", archiveJson, primaryJson));
            return;
        }
        TreeSet<String> paths = new TreeSet<>(before.keySet());
        paths.addAll(after.keySet());
        for (String path : paths) {
            if (!Objects.equals(before.get(path), after.get(path))) {
                fields.add(new FieldDifference(path, before.get(path), after.get(path)));
            }
        }
    }

    private static Map<String, String> flatten(String json) {
        Object root;
        try {
            root = Json.parse(json);
        } catch (RuntimeException e) {
            return null;
        }
        Map<String, String> out = new TreeMap<>();
        walk(root, "payload", out);
        return out;
    }

    @SuppressWarnings("unchecked")
    private static void walk(Object value, String path, Map<String, String> out) {
        if (value instanceof Map<?, ?> map && !map.isEmpty()) {
            for (Map.Entry<String, Object> e : ((Map<String, Object>) map).entrySet()) {
                walk(e.getValue(), path + "." + e.getKey(), out);
            }
        } else if (value instanceof List<?> list && !list.isEmpty()) {
            for (int i = 0; i < list.size(); i++) {
                walk(list.get(i), path + "[" + i + "]", out);
            }
        } else {
            out.put(path, leaf(value));
        }
    }

    private static String leaf(Object value) {
        if (value == null) {
            return "null";
        }
        if (value instanceof String text) {
            StringBuilder sb = new StringBuilder();
            JsonCanonicalizer.writeString(text, sb);
            return sb.toString();
        }
        if (value instanceof Map<?, ?>) {
            return "{}";
        }
        if (value instanceof List<?>) {
            return "[]";
        }
        return JsonCanonicalizer.canonicalize(String.valueOf(value));
    }

    private static final class State {
        final int max;
        final List<EntryDifference> differences = new ArrayList<>();
        boolean truncated;
        long compared, notYetCopied, primaryLast, archiveLast, primaryFirst, onlyInArchiveAtStart;
        String primaryFirstHash;
        boolean startVouched;

        State(int max) {
            this.max = max;
        }

        void add(EntryDifference d) {
            if (differences.size() < max) {
                differences.add(d);
            } else {
                truncated = true;
            }
        }

        void checkRecord(LedgerEntry entry) {
            if (!startVouched && primaryFirst > 1 && LibraryRecords.RETENTION_EVENT.equals(entry.eventType())) {
                startVouched = LibraryRecords.parseRetention(entry.payloadJson())
                        .filter(r -> r.firstSequence() == primaryFirst && r.firstHash().equals(primaryFirstHash))
                        .isPresent();
            }
        }
    }
}
