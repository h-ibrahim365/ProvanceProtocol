package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.checkpoint.FileCheckpointSink;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.Comparator;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.stream.Stream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class CheckpointTests {

    private Path directory;

    private Path tempDirectory() throws IOException {
        directory = Files.createTempDirectory("provance-checkpoints");
        return directory;
    }

    @AfterEach
    void deleteDirectory() throws IOException {
        if (directory == null) {
            return;
        }
        try (Stream<Path> paths = Files.walk(directory)) {
            for (Path p : (Iterable<Path>) paths.sorted(Comparator.reverseOrder())::iterator) {
                Files.deleteIfExists(p);
            }
        }
    }

    @Test
    void noCheckpointForAnEmptyLedger() {
        try (ProvanceLedger ledger = TestLedgers.start()) {
            assertTrue(ledger.createCheckpoint().isEmpty());
        }
    }

    @Test
    void checkpointIsSignedAndReferencesTheHead() {
        try (ProvanceLedger ledger = TestLedgers.start()) {
            ledger.addEntry("EVENT", TestLedgers.payload(1));
            LedgerEntry head = ledger.addEntry("EVENT", TestLedgers.payload(2));
            LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();

            assertEquals(head.sequence(), checkpoint.sequence());
            assertEquals(head.hash(), checkpoint.hash());
            assertEquals(TestLedgers.LEDGER_ID, checkpoint.ledgerId());
            assertNull(ledger.verifier().checkCheckpoint(checkpoint));
        }
    }

    @Test
    void checkpointJsonRoundTrips() {
        try (ProvanceLedger ledger = TestLedgers.start()) {
            ledger.addEntry("EVENT", TestLedgers.payload(1));
            LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();
            String json = checkpoint.toJson();

            assertEquals(checkpoint, LedgerCheckpoint.fromJson(json));
            assertTrue(json.startsWith("{\"v\":1,\"ledgerId\":\"test-ledger\",\"sequence\":1,"), json);
            assertTrue(ledger.verify(LedgerVerificationOptions.withCheckpoint(LedgerCheckpoint.fromJson(json))).valid());
        }
    }

    @Test
    void checkpointWrittenByTheDotNetLibraryIsReadable() {
        // System.Text.Json writes DateTimeOffset with an offset and without fraction when it is zero.
        String dotnet = "{\"v\":1,\"ledgerId\":\"orders-api-test\",\"sequence\":3,"
                + "\"hash\":\"48434bf4cd1fd8132b1a307928755feaba07eec64a96924507685f82a7010b27\","
                + "\"createdAt\":\"2026-01-15T10:00:00+00:00\",\"keyId\":\"k1\","
                + "\"signature\":\"ec11b7a65666c68a44a137bd7dbf9091e3c25d2b581a4a88465f823b9bb30d59\"}";
        LedgerCheckpoint checkpoint = LedgerCheckpoint.fromJson(dotnet);
        assertEquals(3, checkpoint.sequence());
        assertEquals("2026-01-15T10:00:00Z", checkpoint.createdAt().toString());
        assertEquals("7d8027ee48007818e14b2736644856380b3364a2eb957695fea35a8d823aaddb",
                io.github.hibrahim365.provance.crypto.LedgerHashing.computeCheckpointHash(checkpoint));
    }

    @Test
    void fileSinkWritesOncePerSequenceAndReadsTheLatest() throws Exception {
        Path dir = tempDirectory();
        FileCheckpointSink sink = new FileCheckpointSink(dir);
        try (ProvanceLedger ledger = TestLedgers.start()) {
            ledger.addEntry("EVENT", TestLedgers.payload(1));
            LedgerCheckpoint first = ledger.createCheckpoint().orElseThrow();
            sink.publish(first);
            sink.publish(first); // same head: no error, no overwrite

            ledger.addEntry("EVENT", TestLedgers.payload(2));
            LedgerCheckpoint second = ledger.createCheckpoint().orElseThrow();
            sink.publish(second);

            try (Stream<Path> files = Files.list(dir.resolve(TestLedgers.LEDGER_ID))) {
                assertEquals(2, files.count());
            }
            assertTrue(Files.exists(dir.resolve(TestLedgers.LEDGER_ID).resolve("0000000000000002.json")));
            assertEquals(second, FileCheckpointSink.readLatest(dir, TestLedgers.LEDGER_ID).orElseThrow());
            assertTrue(FileCheckpointSink.readLatest(dir, "unknown-ledger").isEmpty());
        }
    }

    @Test
    void publisherPublishesOnlyWhenTheHeadMoves() throws Exception {
        List<LedgerCheckpoint> published = new CopyOnWriteArrayList<>();
        try (ProvanceLedger ledger = TestLedgers.builder(new InMemoryLedgerStore(), o -> o.setCheckpointInterval(Duration.ofMillis(20)))
                .checkpointSink(published::add)
                .build()) {
            Thread.sleep(100);
            assertEquals(0, published.size()); // empty ledger

            ledger.addEntry("EVENT", TestLedgers.payload(1));
            TestLedgers.waitUntil(() -> published.size() == 1);
            Thread.sleep(100);
            assertEquals(1, published.size()); // head did not move

            ledger.addEntry("EVENT", TestLedgers.payload(2));
            TestLedgers.waitUntil(() -> published.size() == 2);
            assertEquals(2, published.get(1).sequence());
        }
    }
}
