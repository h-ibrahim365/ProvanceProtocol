package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.time.Duration;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Behavior of the writer under failures, concurrency and shutdown. */
class WriterTests {

    private final List<ProvanceLedger> opened = new ArrayList<>();
    private final ExecutorService pool = Executors.newFixedThreadPool(16);

    @AfterEach
    void cleanUp() {
        opened.forEach(ProvanceLedger::close);
        pool.shutdownNow();
    }

    private ProvanceLedger open(FaultyStore store, java.util.function.Consumer<ProvanceOptions> configure) {
        ProvanceLedger ledger = TestLedgers.start(store, configure);
        opened.add(ledger);
        return ledger;
    }

    private static void assertChain(ProvanceLedger ledger, int expectedCount) {
        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
        assertEquals(expectedCount, result.entriesVerified());
    }

    @Test
    void lostAcknowledgementDoesNotWedgeTheWriter() {
        FaultyStore store = new FaultyStore();
        store.onAppend = call -> call == 1 ? FaultyStore.Fault.FAIL_AFTER_PERSIST : FaultyStore.Fault.NONE;
        ProvanceLedger ledger = open(store, o -> { });

        List<Long> sequences = new ArrayList<>();
        for (int i = 1; i <= 5; i++) {
            sequences.add(ledger.addEntry("EVENT", TestLedgers.payload(i)).sequence());
        }
        assertEquals(List.of(1L, 2L, 3L, 4L, 5L), sequences);
        assertEquals(5, store.inner.size());
        assertChain(ledger, 5);
    }

    @Test
    void lostAcknowledgementFollowedByAFailedLookupIsStillResolved() {
        FaultyStore store = new FaultyStore();
        store.onAppend = call -> call == 0 ? FaultyStore.Fault.FAIL_AFTER_PERSIST : FaultyStore.Fault.NONE;
        store.failGetById = call -> call == 0;
        ProvanceLedger ledger = open(store, o -> { });

        LedgerEntry entry = ledger.addEntry("EVENT", TestLedgers.payload(1));
        LedgerEntry next = ledger.addEntry("EVENT", TestLedgers.payload(2));
        assertEquals(1, entry.sequence());
        assertEquals(2, next.sequence());
        assertEquals(2, store.inner.size());
        assertChain(ledger, 2);
    }

    @Test
    void partiallyPersistedBatchIsCompletedWithoutDuplicates() throws Exception {
        CountDownLatch gate = new CountDownLatch(1);
        FaultyStore store = new FaultyStore();
        store.onAppend = call -> call == 1 ? FaultyStore.Fault.PERSIST_FIRST_THEN_FAIL : FaultyStore.Fault.NONE;
        store.appendGate = gate::await;
        ProvanceLedger ledger = open(store, o -> { });

        // The first append blocks; the next 20 entries pile up and form one batch.
        CompletableFuture<LedgerEntry> first = ledger.addEntryAsync("EVENT", TestLedgers.payload(0));
        TestLedgers.waitUntil(() -> store.appendCalls() == 1);
        List<CompletableFuture<LedgerEntry>> others = new ArrayList<>();
        for (int i = 1; i <= 20; i++) {
            others.add(ledger.addEntryAsync("EVENT", TestLedgers.payload(i)));
        }
        Thread.sleep(50);
        gate.countDown();

        first.get(10, TimeUnit.SECONDS);
        Set<Long> sequences = new HashSet<>();
        for (CompletableFuture<LedgerEntry> f : others) {
            sequences.add(f.get(10, TimeUnit.SECONDS).sequence());
        }
        assertEquals(20, sequences.size());
        assertEquals(21, store.inner.size());
        assertChain(ledger, 21);
    }

    @Test
    void transientFailuresAreRetried() {
        FaultyStore store = new FaultyStore();
        store.onAppend = call -> call < 2 ? FaultyStore.Fault.FAIL_BEFORE : FaultyStore.Fault.NONE;
        ProvanceLedger ledger = open(store, o -> { });

        assertEquals(1, ledger.addEntry("EVENT", TestLedgers.payload(1)).sequence());
        assertEquals(3, store.appendCalls());
    }

    @Test
    void persistentFailureReachesTheCallerAndTheWriterRecovers() {
        FaultyStore store = new FaultyStore();
        store.onAppend = call -> call < 3 ? FaultyStore.Fault.FAIL_BEFORE : FaultyStore.Fault.NONE;
        ProvanceLedger ledger = open(store, o -> o.setMaxAppendAttempts(3));

        LedgerUnavailableException ex = assertThrows(LedgerUnavailableException.class,
                () -> ledger.addEntry("EVENT", TestLedgers.payload(1)));
        assertInstanceOf(io.github.hibrahim365.provance.store.LedgerStoreException.class, ex.getCause());

        assertEquals(1, ledger.addEntry("EVENT", TestLedgers.payload(2)).sequence());
        assertChain(ledger, 1);
    }

    @Test
    void concurrentProducersProduceALinearChain() throws Exception {
        ProvanceLedger ledger = TestLedgers.start(new InMemoryLedgerStore());
        opened.add(ledger);
        List<Future<LedgerEntry>> futures = new ArrayList<>();
        for (int i = 0; i < 2000; i++) {
            int n = i;
            futures.add(pool.submit(() -> ledger.addEntry("EVENT", TestLedgers.payload(n))));
        }
        Set<Long> sequences = new HashSet<>();
        for (Future<LedgerEntry> f : futures) {
            sequences.add(f.get(30, TimeUnit.SECONDS).sequence());
        }
        assertEquals(2000, sequences.size());
        assertTrue(sequences.contains(1L) && sequences.contains(2000L));
        assertChain(ledger, 2000);
    }

    @Test
    void twoApplicationInstancesCanShareOneLedger() throws Exception {
        InMemoryLedgerStore shared = new InMemoryLedgerStore();
        ProvanceLedger instanceA = TestLedgers.start(shared, o -> o.setMaxBatchSize(8));
        ProvanceLedger instanceB = TestLedgers.start(shared, o -> o.setMaxBatchSize(8));
        opened.add(instanceA);
        opened.add(instanceB);

        List<Future<LedgerEntry>> futures = new ArrayList<>();
        for (int i = 0; i < 500; i++) {
            int n = i;
            futures.add(pool.submit(() -> instanceA.addEntry("FROM_A", TestLedgers.payload(n))));
            futures.add(pool.submit(() -> instanceB.addEntry("FROM_B", TestLedgers.payload(n))));
        }
        Set<Long> sequences = new HashSet<>();
        for (Future<LedgerEntry> f : futures) {
            sequences.add(f.get(30, TimeUnit.SECONDS).sequence());
        }
        assertEquals(1000, sequences.size());
        assertEquals(1000, shared.size());
        assertChain(instanceA, 1000);
    }

    @Test
    void closeDrainsQueuedEntries() throws Exception {
        FaultyStore store = new FaultyStore();
        store.appendGate = () -> Thread.sleep(5);
        ProvanceLedger ledger = TestLedgers.start(store, o -> o.setMaxBatchSize(1));

        List<CompletableFuture<LedgerEntry>> pending = new ArrayList<>();
        for (int i = 0; i < 50; i++) {
            pending.add(ledger.addEntryAsync("EVENT", TestLedgers.payload(i)));
        }
        ledger.close();

        for (CompletableFuture<LedgerEntry> f : pending) {
            f.get(10, TimeUnit.SECONDS);
        }
        assertEquals(50, store.inner.size());
    }

    @Test
    void shutdownTimeoutFailsPendingEntriesInsteadOfHanging() throws Exception {
        FaultyStore store = new FaultyStore();
        store.appendGate = () -> Thread.sleep(Long.MAX_VALUE);
        ProvanceLedger ledger = TestLedgers.start(store, o -> o.setShutdownTimeout(Duration.ofMillis(200)));

        List<CompletableFuture<LedgerEntry>> pending = new ArrayList<>();
        for (int i = 0; i < 5; i++) {
            pending.add(ledger.addEntryAsync("EVENT", TestLedgers.payload(i)));
        }
        TestLedgers.waitUntil(() -> store.appendCalls() == 1);
        long start = System.nanoTime();
        ledger.close();
        assertTrue(System.nanoTime() - start < TimeUnit.SECONDS.toNanos(5), "close() must not hang");

        for (CompletableFuture<LedgerEntry> f : pending) {
            ExecutionException ex = assertThrows(ExecutionException.class, () -> f.get(10, TimeUnit.SECONDS));
            assertInstanceOf(LedgerUnavailableException.class, ex.getCause());
        }
    }

    @Test
    void entriesAreRefusedOnceTheLedgerIsClosed() {
        ProvanceLedger ledger = TestLedgers.start(new InMemoryLedgerStore());
        ledger.close();
        assertThrows(LedgerUnavailableException.class, () -> ledger.addEntry("EVENT", TestLedgers.payload(1)));
        ledger.close(); // idempotent
    }

    @Test
    void fullQueueFailsFastWhenConfigured() throws Exception {
        CountDownLatch gate = new CountDownLatch(1);
        FaultyStore store = new FaultyStore();
        store.appendGate = gate::await;
        ProvanceLedger ledger = open(store, o -> {
            o.setQueueCapacity(1);
            o.setQueueFullBehavior(QueueFullBehavior.FAIL_FAST);
        });

        CompletableFuture<LedgerEntry> inWriter = ledger.addEntryAsync("EVENT", TestLedgers.payload(1));
        TestLedgers.waitUntil(() -> store.appendCalls() == 1);
        CompletableFuture<LedgerEntry> inQueue = ledger.addEntryAsync("EVENT", TestLedgers.payload(2));

        assertThrows(LedgerOverloadedException.class, () -> ledger.addEntry("EVENT", TestLedgers.payload(3)));
        ExecutionException async = assertThrows(ExecutionException.class,
                () -> ledger.addEntryAsync("EVENT", TestLedgers.payload(4)).get(1, TimeUnit.SECONDS));
        assertInstanceOf(LedgerOverloadedException.class, async.getCause());

        gate.countDown();
        inWriter.get(10, TimeUnit.SECONDS);
        inQueue.get(10, TimeUnit.SECONDS);
    }

    @Test
    void writerRefusesAStoreThatHoldsAnotherLedger() {
        FaultyStore store = new FaultyStore();
        try (ProvanceLedger other = TestLedgers.start(store, o -> o.setLedgerId("another-ledger"))) {
            other.addEntry("EVENT", TestLedgers.payload(1));
        }
        ProvanceLedger ledger = open(store, o -> o.setMaxAppendAttempts(2));

        LedgerUnavailableException ex = assertThrows(LedgerUnavailableException.class,
                () -> ledger.addEntry("EVENT", TestLedgers.payload(2)));
        assertTrue(ex.getCause().getMessage().contains("another-ledger"), ex.getCause().getMessage());
    }

    @Test
    void interruptedCallersDoNotCorruptTheChain() throws Exception {
        ProvanceLedger ledger = TestLedgers.start(new InMemoryLedgerStore());
        opened.add(ledger);
        List<Future<?>> callers = new ArrayList<>();
        for (int i = 0; i < 200; i++) {
            int n = i;
            callers.add(pool.submit(() -> {
                try {
                    ledger.addEntry("EVENT", TestLedgers.payload(n));
                } catch (LedgerUnavailableException ignored) {
                    // interrupted while waiting: the entry may or may not be written
                }
            }));
        }
        for (Future<?> f : callers) {
            f.cancel(true);
        }
        ledger.addEntry("EVENT", TestLedgers.payload(999));
        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
    }

    @Test
    void asyncCallbacksDoNotRunOnTheWriterThread() throws Exception {
        ProvanceLedger ledger = TestLedgers.start(new InMemoryLedgerStore());
        opened.add(ledger);
        String thread = ledger.addEntryAsync("EVENT", TestLedgers.payload(1))
                .thenApply(e -> Thread.currentThread().getName())
                .get(10, TimeUnit.SECONDS);
        assertTrue(!thread.startsWith("provance-writer"), thread);
    }
}
