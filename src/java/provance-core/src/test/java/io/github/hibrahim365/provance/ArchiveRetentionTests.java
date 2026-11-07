package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.compare.DifferenceKind;
import io.github.hibrahim365.provance.compare.EntryDifference;
import io.github.hibrahim365.provance.compare.FieldDifference;
import io.github.hibrahim365.provance.compare.LedgerComparison;
import io.github.hibrahim365.provance.compare.OriginalCopy;
import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.store.LedgerStore;
import io.github.hibrahim365.provance.store.StoreAccess;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.LedgerVerifier;
import io.github.hibrahim365.provance.verification.VerificationFailure;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneId;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.function.Consumer;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** 0.2: archive copy, field-level comparison, recorded erasures and retention. Same scenarios as the .NET and Python suites. */
class ArchiveRetentionTests {

    private final List<ProvanceLedger> open = new ArrayList<>();

    @AfterEach
    void closeLedgers() {
        open.forEach(ProvanceLedger::close);
    }

    private ProvanceLedger start(LedgerStore store, LedgerStore archive, Clock clock, Consumer<ProvanceOptions> configure) {
        ProvanceLedger.Builder builder = TestLedgers.builder(store, configure);
        if (archive != null) {
            builder.archive(archive);
        }
        if (clock != null) {
            builder.clock(clock);
        }
        ProvanceLedger ledger = builder.build();
        open.add(ledger);
        return ledger;
    }

    private ProvanceLedger start(LedgerStore store, LedgerStore archive) {
        return start(store, archive, null, o -> { });
    }

    private static List<LedgerEntry> refunds(ProvanceLedger ledger, int count) {
        List<LedgerEntry> entries = new ArrayList<>();
        for (int i = 1; i <= count; i++) {
            entries.add(ledger.addEntry("REFUND", new AuditedPayload("support-" + i, null, null,
                    Map.of("orderId", "A-" + (1000 + i), "amount", "49.90"))));
        }
        return entries;
    }

    private static LedgerVerifier archiveVerifier(LedgerStore archive) {
        return new LedgerVerifier(archive, new HmacLedgerSigner("k1", TestLedgers.SECRET), TestLedgers.LEDGER_ID);
    }

    private static List<LedgerEntry> all(LedgerStore store) {
        try (Stream<LedgerEntry> entries = store.read(1)) {
            return entries.collect(Collectors.toList());
        }
    }

    // Archive copy and comparison

    @Test
    void entriesAreCopiedToTheArchiveAndVerifyThere() {
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(new InMemoryLedgerStore(), archive);
        refunds(ledger, 5);

        LedgerComparison comparison = ledger.compareWithArchive();

        assertTrue(comparison.identical(), comparison.describe());
        assertEquals(5, comparison.compared());
        ArchiveStatus status = ledger.archiveStatus().orElseThrow();
        assertTrue(status.healthy());
        assertEquals(5, status.archivedThrough());
        assertTrue(archiveVerifier(archive).verify().valid());
    }

    @Test
    void theLastEntriesAreCopiedOnClose() {
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(new InMemoryLedgerStore(), archive, null, o -> o.setArchiveInterval(Duration.ofHours(1)));
        for (int i = 0; i < 30; i++) {
            ledger.addEntryAsync("EVENT", TestLedgers.payload(i));
        }

        ledger.close();

        assertEquals(30, archive.size());
    }

    @Test
    void aChangedValueShowsItsOriginalFromTheArchive() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, new InMemoryLedgerStore());
        refunds(ledger, 4);
        ledger.compareWithArchive();
        StoreAccess.tamper(store, 2, e -> {
            String forged = e.payloadJson().replace("\"49.90\"", "\"4990.00\"");
            return Edit.payload(e, forged, e.payloadHash());
        });

        assertEquals(VerificationFailure.PAYLOAD_HASH_MISMATCH, ledger.verify().failure());
        LedgerComparison comparison = ledger.compareWithArchive();
        assertEquals(1, comparison.differences().size());
        EntryDifference difference = comparison.differences().get(0);
        assertEquals(2, difference.sequence());
        assertEquals(DifferenceKind.CHANGED, difference.kind());
        assertEquals(OriginalCopy.ARCHIVE, difference.original());
        assertTrue(difference.isTampering());
        assertEquals(1, difference.fields().size());
        FieldDifference field = difference.fields().get(0);
        assertEquals("payload.data.amount", field.field());
        assertEquals("\"49.90\"", field.archiveValue());
        assertEquals("\"4990.00\"", field.primaryValue());
        assertTrue(comparison.describe().contains("payload.data.amount: \"49.90\" -> \"4990.00\""), comparison.describe());
    }

    @Test
    void deletedEntriesAndAChangeInTheArchiveAreAttributed() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, archive);
        refunds(ledger, 6);
        ledger.compareWithArchive();
        StoreAccess.delete(store, e -> e.sequence() == 3 || e.sequence() == 6);
        StoreAccess.tamper(archive, 4, e -> Edit.eventType(e, "EDITED_IN_THE_ARCHIVE"));

        Map<Long, EntryDifference> differences = new HashMap<>();
        for (EntryDifference d : ledger.compareWithArchive().differences()) {
            differences.put(d.sequence(), d);
        }

        assertEquals(DifferenceKind.MISSING_FROM_PRIMARY, differences.get(3L).kind());
        assertEquals(DifferenceKind.MISSING_FROM_PRIMARY, differences.get(6L).kind());
        assertEquals(OriginalCopy.PRIMARY, differences.get(4L).original());
    }

    @Test
    void anEntryModifiedBeforeItWasCopiedIsNotCopied() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        try (ProvanceLedger first = TestLedgers.start(store)) {
            for (int i = 0; i < 4; i++) {
                first.addEntry("EVENT", TestLedgers.payload(i));
            }
        }
        StoreAccess.tamper(store, 3, e -> Edit.eventType(e, "FORGED"));

        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, archive);
        ledger.compareWithArchive();
        ArchiveStatus status = ledger.archiveStatus().orElseThrow();

        assertEquals(2, archive.size());
        assertFalse(status.healthy());
        assertEquals(2, status.archivedThrough());
        assertTrue(status.problem().contains("Entry 3"), status.problem());
    }

    @Test
    void archiveAndPrimaryCannotBeTheSameStore() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        assertThrows(IllegalStateException.class, () -> start(store, store));
    }

    // Recorded erasures

    @Test
    void anErasureAppendsARecordAndErasesBothCopies() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, archive);
        List<LedgerEntry> entries = refunds(ledger, 2);
        ledger.compareWithArchive();

        assertTrue(ledger.redactPayload(entries.get(0).id()));

        LedgerEntry head = store.getHead().orElseThrow();
        assertEquals(LibraryRecords.REDACTION_EVENT, head.eventType());
        assertTrue(head.payloadJson().contains(entries.get(0).id().toString()));
        assertFalse(head.payloadJson().contains("support-1"));
        assertNull(store.getById(entries.get(0).id()).orElseThrow().payloadJson());
        ledger.compareWithArchive();
        assertNull(archive.getById(entries.get(0).id()).orElseThrow().payloadJson());
        assertTrue(ledger.verify().valid());
        assertTrue(archiveVerifier(archive).verify().valid());
    }

    @Test
    void aPayloadSetToNullInTheDatabaseIsDetectedAndNotLaundered() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, null);
        List<LedgerEntry> entries = refunds(ledger, 3);
        store.redactPayload(entries.get(2).id()); // UPDATE ... SET payload = NULL

        LedgerVerificationResult result = ledger.verify();
        assertEquals(VerificationFailure.UNRECORDED_REDACTION, result.failure());
        assertEquals(3L, (long) result.failedAtSequence());
        assertTrue(ledger.verify(LedgerVerificationOptions.defaults().acceptingUnrecordedRedactions()).valid());

        assertTrue(ledger.redactPayload(entries.get(2).id())); // already missing: no record is written
        assertEquals(3, store.size());
        assertEquals(VerificationFailure.UNRECORDED_REDACTION, ledger.verify().failure());
    }

    @Test
    void anArchiveThatRefusesTheErasureIsReported() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = start(store, new RefusingArchive());
        LedgerEntry entry = refunds(ledger, 1).get(0);
        ledger.compareWithArchive();

        LedgerArchiveException error = assertThrows(LedgerArchiveException.class, () -> ledger.redactPayload(entry.id()));

        assertTrue(error.getMessage().contains("not in the archive"), error.getMessage());
        assertNull(store.getById(entry.id()).orElseThrow().payloadJson());
    }

    @Test
    void libraryEventTypesAreReserved() {
        ProvanceLedger ledger = start(new InMemoryLedgerStore(), null);
        assertThrows(IllegalArgumentException.class, () -> ledger.addEntry("provance.retention", TestLedgers.payload(1)));
        assertThrows(IllegalArgumentException.class, () -> ledger.addEntry("PROVANCE.anything", TestLedgers.payload(1)));
    }

    // Retention

    /** Five entries on day 0, three on day 10. */
    private ProvanceLedger retentionSetup(MutableClock clock, InMemoryLedgerStore store, InMemoryLedgerStore archive,
                                          Consumer<ProvanceOptions> configure) {
        ProvanceLedger ledger = start(store, archive, clock, configure);
        for (int i = 1; i <= 8; i++) {
            if (i == 6) {
                clock.advance(Duration.ofDays(10));
            }
            ledger.addEntry("EVENT", TestLedgers.payload(i));
        }
        return ledger;
    }

    @Test
    void oldEntriesLeaveThePrimaryStoreAndTheLedgerStillVerifies() {
        MutableClock clock = new MutableClock();
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        InMemoryLedgerStore archive = new InMemoryLedgerStore();
        ProvanceLedger ledger = retentionSetup(clock, store, archive, o -> { });

        RetentionResult result = ledger.applyRetention(Duration.ofDays(7));

        assertTrue(result.applied(), result.message());
        assertEquals(4, result.removedEntries());
        assertEquals(5, result.firstSequence());
        assertEquals(LibraryRecords.RETENTION_EVENT, result.record().eventType());
        assertEquals(9, result.record().sequence());

        LedgerVerificationResult verification = ledger.verify(LedgerVerificationOptions.withCheckpoint(result.checkpoint()));
        assertTrue(verification.valid(), verification.message());
        assertEquals(5, verification.firstSequence());
        assertTrue(verification.message().contains("removed by retention"), verification.message());

        LedgerComparison comparison = ledger.compareWithArchive();
        assertTrue(comparison.identical(), comparison.describe());
        assertEquals(4, comparison.removedByRetention());

        LedgerVerificationResult full = archiveVerifier(archive).verify();
        assertTrue(full.valid(), full.message());
        assertEquals(1, full.firstSequence());
        assertEquals(9, full.lastSequence());
    }

    @Test
    void retentionRunsAgainAndMovesTheStartForward() {
        MutableClock clock = new MutableClock();
        ProvanceLedger ledger = retentionSetup(clock, new InMemoryLedgerStore(), new InMemoryLedgerStore(), o -> { });
        assertTrue(ledger.applyRetention(Duration.ofDays(7)).applied());
        clock.advance(Duration.ofDays(10));
        ledger.addEntry("EVENT", TestLedgers.payload(10));

        RetentionResult second = ledger.applyRetention(Duration.ofDays(7));

        assertTrue(second.applied(), second.message());
        assertEquals(9, second.firstSequence());
        assertTrue(ledger.verify().valid());
    }

    @Test
    void nothingIsRemovedWhenItIsNotSafe() {
        for (String scenario : List.of("nothingOld", "noArchive", "tamperedPrimary", "differentArchive")) {
            MutableClock clock = new MutableClock();
            InMemoryLedgerStore store = new InMemoryLedgerStore();
            InMemoryLedgerStore archive = new InMemoryLedgerStore();
            ProvanceLedger ledger = retentionSetup(clock, store, scenario.equals("noArchive") ? null : archive, o -> { });
            if (scenario.equals("tamperedPrimary")) {
                StoreAccess.tamper(store, 2, e -> Edit.eventType(e, "FORGED"));
            }
            if (scenario.equals("differentArchive")) {
                ledger.compareWithArchive();
                StoreAccess.tamper(archive, 3, e -> Edit.eventType(e, "EDITED_IN_THE_ARCHIVE"));
            }

            RetentionResult result = ledger.applyRetention(Duration.ofDays(scenario.equals("nothingOld") ? 30 : 7));

            assertFalse(result.applied(), scenario);
            assertEquals(8, store.size(), scenario);
        }
    }

    @Test
    void retentionWithoutAnArchiveWhenAllowed() {
        MutableClock clock = new MutableClock();
        ProvanceLedger ledger = retentionSetup(clock, new InMemoryLedgerStore(), null, o -> o.setRetentionRequireArchive(false));

        assertTrue(ledger.applyRetention(Duration.ofDays(7)).applied());
        assertTrue(ledger.verify().valid());
    }

    @Test
    void retentionNeedsAStoreThatCanDeleteEntries() {
        ProvanceLedger ledger = start(new RefusingArchive(), null);
        assertThrows(UnsupportedOperationException.class, () -> ledger.applyRetention(Duration.ofDays(7)));
    }

    @Test
    void deletingTheFirstEntriesWithoutARecordIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = retentionSetup(new MutableClock(), store, new InMemoryLedgerStore(), o -> { });
        StoreAccess.delete(store, e -> e.sequence() < 3);

        LedgerVerificationResult result = ledger.verify();

        assertEquals(VerificationFailure.SEQUENCE_GAP, result.failure());
        assertEquals(1L, (long) result.failedAtSequence());
    }

    @Test
    void aForgedRetentionRecordNeedsTheKey() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = retentionSetup(new MutableClock(), store, new InMemoryLedgerStore(), o -> { });
        StoreAccess.delete(store, e -> e.sequence() < 5);
        LedgerEntry anchor = all(store).get(0);
        String payload = "{\"firstHash\":\"" + anchor.hash() + "\",\"firstSequence\":" + anchor.sequence() + "}";
        StoreAccess.tamper(store, 8, e -> {
            LedgerEntry forged = Edit.payload(Edit.eventType(e, LibraryRecords.RETENTION_EVENT), payload, LedgerHashing.sha256Hex(payload));
            return Edit.hash(forged, LedgerHashing.computeEntryHash(forged));
        });

        LedgerVerificationResult result = ledger.verify();

        assertEquals(VerificationFailure.INVALID_SIGNATURE, result.failure());
        assertEquals(8L, (long) result.failedAtSequence());
    }

    @Test
    void retentionRunsOnItsOwnWhenConfigured() throws InterruptedException {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = retentionSetup(new MutableClock(), store, new InMemoryLedgerStore(), o -> {
            o.setRetentionKeepFor(Duration.ofDays(7));
            o.setRetentionInterval(Duration.ofMillis(50));
        });

        TestLedgers.waitUntil(() -> all(store).get(0).sequence() == 5);

        assertTrue(ledger.verify().valid());
    }

    /** Clock moved by the test. */
    private static final class MutableClock extends Clock {
        private volatile Instant now = Instant.parse("2026-11-01T08:00:00Z");

        void advance(Duration duration) {
            now = now.plus(duration);
        }

        @Override
        public ZoneId getZone() {
            return ZoneOffset.UTC;
        }

        @Override
        public Clock withZone(ZoneId zone) {
            return this;
        }

        @Override
        public Instant instant() {
            return now;
        }
    }

    /** Archive whose account has no UPDATE permission (and a store that cannot delete entries). */
    private static final class RefusingArchive implements LedgerStore {
        private final InMemoryLedgerStore inner = new InMemoryLedgerStore();

        @Override
        public Optional<LedgerEntry> getHead() {
            return inner.getHead();
        }

        @Override
        public void append(List<LedgerEntry> entries) {
            inner.append(entries);
        }

        @Override
        public Optional<LedgerEntry> getById(UUID id) {
            return inner.getById(id);
        }

        @Override
        public Stream<LedgerEntry> read(long fromSequence) {
            return inner.read(fromSequence);
        }

        @Override
        public boolean redactPayload(UUID id) {
            throw new IllegalStateException("UPDATE permission denied on table provance_archive");
        }
    }
}
