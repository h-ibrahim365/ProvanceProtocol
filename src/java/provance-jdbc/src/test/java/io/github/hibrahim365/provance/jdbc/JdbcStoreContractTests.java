package io.github.hibrahim365.provance.jdbc;

import io.github.hibrahim365.provance.AuditedPayload;
import io.github.hibrahim365.provance.LedgerConcurrencyException;
import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.ProvanceLedger;
import io.github.hibrahim365.provance.RetentionResult;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.VerificationFailure;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.sql.Connection;
import java.sql.Statement;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import javax.sql.DataSource;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Behavior every database must show. Each test uses its own table, so one database (or container) serves a whole
 * class.
 */
abstract class JdbcStoreContractTests {

    static final String SECRET = "unit-test-secret-key-0123456789abcdef";

    private final List<ProvanceLedger> opened = new ArrayList<>();
    private final String table = "provance_t" + UUID.randomUUID().toString().replace("-", "").substring(0, 12);

    /** The database under test. May skip the test (for example when Docker is not available). */
    protected abstract DataSource dataSource();

    protected abstract SqlDialect expectedDialect();

    /** Writes per instance in the concurrency test; SQLite serializes writers, so it uses fewer. */
    protected int concurrentWritesPerInstance() {
        return 100;
    }

    @AfterEach
    void closeLedgers() {
        opened.forEach(ProvanceLedger::close);
    }

    protected JdbcLedgerStore store() {
        return store(3);
    }

    protected JdbcLedgerStore store(int readBatchSize) {
        JdbcStoreOptions options = new JdbcStoreOptions();
        options.setTableName(table);
        options.setReadBatchSize(readBatchSize);
        return new JdbcLedgerStore(dataSource(), options);
    }

    protected ProvanceLedger ledger(JdbcLedgerStore store) {
        ProvanceLedger ledger = ProvanceLedger.builder()
                .ledgerId("jdbc-test")
                .secretKey(SECRET)
                .configure(o -> {
                    o.setRetryBaseDelay(Duration.ofMillis(5));
                    o.setMaxAppendAttempts(10);
                })
                .store(store)
                .build();
        opened.add(ledger);
        return ledger;
    }

    @Test
    void schemaIsCreatedAndEntriesRoundTripExactly() {
        JdbcLedgerStore store = store();
        ProvanceLedger ledger = ledger(store);
        assertEquals(expectedDialect(), store.dialect());

        LedgerEntry written = ledger.addEntry("NOTE_ADDED", Map.of(
                "text", "Café € \"quoted\" back\\slash\nline 😀",
                "amount", "49.90",
                "count", 3));
        ledger.addEntry("NOTE_ADDED", AuditedPayload.of("user-1", "second"));

        // A fresh store instance reads exactly what was written: same payload text, same millisecond timestamp.
        JdbcLedgerStore fresh = store();
        LedgerEntry read = fresh.getById(written.id()).orElseThrow();
        assertEquals(written, read);
        assertEquals(2, fresh.getHead().orElseThrow().sequence());
        assertTrue(fresh.getById(UUID.randomUUID()).isEmpty());

        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
        assertEquals(2, result.entriesVerified());
    }

    @Test
    void duplicateSequenceOrIdIsReportedAsAConflict() {
        JdbcLedgerStore store = store();
        ProvanceLedger ledger = ledger(store);
        LedgerEntry first = ledger.addEntry("EVENT", AuditedPayload.of("u", "1"));

        LedgerEntry sameSequence = copy(first, UUID.randomUUID(), first.sequence());
        assertThrows(LedgerConcurrencyException.class, () -> store.append(List.of(sameSequence)));

        LedgerEntry sameId = copy(first, first.id(), first.sequence() + 1);
        assertThrows(LedgerConcurrencyException.class, () -> store.append(List.of(sameId)));

        assertEquals(1, store.getHead().orElseThrow().sequence());
    }

    @Test
    void aFailedBatchIsNotPartiallyPersisted() {
        JdbcLedgerStore store = store();
        ProvanceLedger ledger = ledger(store);
        LedgerEntry first = ledger.addEntry("EVENT", AuditedPayload.of("u", "1"));
        ledger.addEntry("EVENT", AuditedPayload.of("u", "2"));

        // Sequence 3 is new, sequence 4 reuses the id of entry 1: the whole batch must be rejected.
        LedgerEntry third = copy(first, UUID.randomUUID(), 3);
        LedgerEntry fourth = copy(first, first.id(), 4);
        assertThrows(LedgerConcurrencyException.class, () -> store.append(List.of(third, fourth)));

        assertEquals(2, store.getHead().orElseThrow().sequence());
        assertTrue(store.getById(third.id()).isEmpty());
    }

    @Test
    void readingStreamsPageByPage() {
        JdbcLedgerStore store = store(3);
        ProvanceLedger ledger = ledger(store);
        for (int i = 0; i < 10; i++) {
            ledger.addEntry("EVENT", AuditedPayload.of("u", "#" + i));
        }

        List<Long> all;
        try (Stream<LedgerEntry> entries = store.read(1)) {
            all = entries.map(LedgerEntry::sequence).collect(Collectors.toList());
        }
        assertEquals(List.of(1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L), all);

        try (Stream<LedgerEntry> entries = store.read(5)) {
            assertEquals(6, entries.count());
        }
        try (Stream<LedgerEntry> entries = store.read(11)) {
            assertEquals(0, entries.count());
        }
    }

    @Test
    void redactionKeepsTheChainValid() {
        JdbcLedgerStore store = store();
        ProvanceLedger ledger = ledger(store);
        LedgerEntry entry = ledger.addEntry("EVENT", AuditedPayload.of("alice@example.com", "personal data"));
        ledger.addEntry("EVENT", AuditedPayload.of("u", "after"));

        assertTrue(ledger.redactPayload(entry.id()));
        assertFalse(ledger.redactPayload(UUID.randomUUID()));

        LedgerEntry redacted = store.getById(entry.id()).orElseThrow();
        assertTrue(redacted.isRedacted());
        assertEquals(entry.payloadHash(), redacted.payloadHash());

        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
        assertEquals(1, result.redactedPayloads());
    }

    @Test
    void archiveTableAndRetentionKeepTheLedgerVerifiable() {
        JdbcLedgerStore store = store();
        JdbcStoreOptions archiveOptions = new JdbcStoreOptions();
        archiveOptions.setTableName(table + "_a");
        JdbcLedgerStore archive = new JdbcLedgerStore(dataSource(), archiveOptions);

        // Three entries written 30 days ago, then two now.
        try (ProvanceLedger old = ProvanceLedger.builder().ledgerId("jdbc-test").secretKey(SECRET).store(store)
                .clock(Clock.fixed(Instant.now().minus(Duration.ofDays(30)), ZoneOffset.UTC)).build()) {
            for (int i = 0; i < 3; i++) {
                old.addEntry("EVENT", AuditedPayload.of("u", "old " + i));
            }
        }
        ProvanceLedger ledger = ProvanceLedger.builder().ledgerId("jdbc-test").secretKey(SECRET).store(store)
                .archive(archive).build();
        opened.add(ledger);
        ledger.addEntry("EVENT", AuditedPayload.of("u", "new 1"));
        ledger.addEntry("EVENT", AuditedPayload.of("u", "new 2"));

        RetentionResult retention = ledger.applyRetention(Duration.ofDays(7));

        assertTrue(retention.applied(), retention.message());
        assertEquals(2, retention.removedEntries());
        assertEquals(3, retention.firstSequence());
        try (Stream<LedgerEntry> entries = store.read(1)) {
            assertEquals(3, entries.findFirst().orElseThrow().sequence());
        }
        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
        assertEquals(3, result.firstSequence());
        assertTrue(ledger.compareWithArchive().identical());
        assertEquals(6, archive.getHead().orElseThrow().sequence());
    }

    @Test
    void twoInstancesShareALedgerAndTamperingInTheDatabaseIsDetected() throws Exception {
        ProvanceLedger instanceA = ledger(store(50));
        ProvanceLedger instanceB = ledger(store(50));
        int perInstance = concurrentWritesPerInstance();

        ExecutorService pool = Executors.newFixedThreadPool(8);
        try {
            List<Future<LedgerEntry>> writes = new ArrayList<>();
            for (int i = 0; i < perInstance; i++) {
                int n = i;
                writes.add(pool.submit(() -> instanceA.addEntry("FROM_A", AuditedPayload.of("a", "#" + n))));
                writes.add(pool.submit(() -> instanceB.addEntry("FROM_B", AuditedPayload.of("b", "#" + n))));
            }
            Set<Long> sequences = new HashSet<>();
            for (Future<LedgerEntry> f : writes) {
                sequences.add(f.get(120, TimeUnit.SECONDS).sequence());
            }
            assertEquals(2 * perInstance, sequences.size());
        } finally {
            pool.shutdownNow();
        }

        LedgerVerificationResult valid = instanceA.verify();
        assertTrue(valid.valid(), valid.message());
        assertEquals(2L * perInstance, valid.entriesVerified());

        // Someone with database access edits a row directly.
        try (Connection c = dataSource().getConnection(); Statement st = c.createStatement()) {
            st.executeUpdate("UPDATE " + table + " SET event_type = 'ROLE_REVOKED' WHERE seq = 2");
            if (!c.getAutoCommit()) {
                c.commit();
            }
        }
        LedgerVerificationResult tampered = instanceB.verify();
        assertEquals(VerificationFailure.HASH_MISMATCH, tampered.failure());
        assertEquals(2, tampered.failedAtSequence().longValue());
    }

    @Test
    void anExistingTableIsReusedAndTheStatementsAreExposedForMigrations() {
        JdbcLedgerStore first = store();
        ledger(first).addEntry("EVENT", AuditedPayload.of("u", "1"));

        JdbcLedgerStore second = store();
        assertEquals(1, second.getHead().orElseThrow().sequence());

        List<String> statements = second.createTableStatements();
        assertEquals(2, statements.size());
        assertTrue(statements.get(0).startsWith("CREATE TABLE " + table + " ("), statements.get(0));
        assertTrue(statements.get(1).contains("UNIQUE INDEX " + table + "_seq_unique"), statements.get(1));
    }

    private static LedgerEntry copy(LedgerEntry e, UUID id, long sequence) {
        return new LedgerEntry(e.formatVersion(), e.ledgerId(), sequence, id, e.timestamp(), e.eventType(), e.payloadJson(),
                e.payloadHash(), e.previousHash(), e.keyId(), e.hash(), e.signature());
    }
}
