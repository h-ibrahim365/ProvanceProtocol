package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.store.StoreAccess;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.LedgerVerifier;
import io.github.hibrahim365.provance.verification.VerificationFailure;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** What an attacker with write access to the database can and cannot do unnoticed. */
class IntegrityTests {

    private final List<ProvanceLedger> opened = new ArrayList<>();

    @AfterEach
    void closeLedgers() {
        opened.forEach(ProvanceLedger::close);
    }

    private ProvanceLedger open(InMemoryLedgerStore store) {
        ProvanceLedger ledger = TestLedgers.start(store);
        opened.add(ledger);
        return ledger;
    }

    private ProvanceLedger withEntries(InMemoryLedgerStore store, int count) {
        ProvanceLedger ledger = open(store);
        for (int i = 1; i <= count; i++) {
            ledger.addEntry("EVENT", AuditedPayload.of("user-" + i, "#" + i));
        }
        return ledger;
    }

    @Test
    void validChainVerifies() {
        ProvanceLedger ledger = withEntries(new InMemoryLedgerStore(), 10);
        LedgerVerificationResult result = ledger.verify();
        assertTrue(result.valid(), result.message());
        assertEquals(10, result.entriesVerified());
        assertEquals(10, result.lastSequence());
    }

    @Test
    void emptyLedgerIsValid() {
        LedgerVerificationResult result = open(new InMemoryLedgerStore()).verify();
        assertTrue(result.valid());
        assertEquals(0, result.entriesVerified());
    }

    @Test
    void firstEntryLinksToTheDerivedGenesisHash() {
        ProvanceLedger ledger = withEntries(new InMemoryLedgerStore(), 1);
        LedgerEntry head = ledger.getHead().orElseThrow();
        assertEquals(LedgerHashing.deriveGenesisHash(TestLedgers.LEDGER_ID), head.previousHash());
        assertEquals(1, head.sequence());
    }

    /** A payload type with fields of its own, like the HTTP payload of the Spring filter. */
    public record HttpLikePayload(String actorId, String path, String clientIp) {
    }

    @Test
    void everyFieldOfThePayloadTypeIsSigned() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = open(store);
        LedgerEntry entry = ledger.addEntry("HTTP", new HttpLikePayload("u1", "/api/pay", "10.0.0.1"));
        assertTrue(entry.payloadJson().contains("\"path\":\"/api/pay\""));
        assertTrue(entry.payloadJson().contains("\"clientIp\":\"10.0.0.1\""));

        StoreAccess.tamper(store, entry.sequence(),
                e -> Edit.payload(e, e.payloadJson().replace("/api/pay", "/api/refund"), e.payloadHash()));

        LedgerVerificationResult result = ledger.verify();
        assertFalse(result.valid());
        assertEquals(VerificationFailure.PAYLOAD_HASH_MISMATCH, result.failure());
        assertEquals(entry.sequence(), result.failedAtSequence().longValue());
    }

    @Test
    void modifiedEventTypeIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 5);
        StoreAccess.tamper(store, 3, e -> Edit.eventType(e, "SOMETHING_ELSE"));

        LedgerVerificationResult result = ledger.verify();
        assertEquals(VerificationFailure.HASH_MISMATCH, result.failure());
        assertEquals(3, result.failedAtSequence().longValue());
        assertEquals(2, result.entriesVerified());
    }

    @Test
    void payloadRewrittenWithAMatchingPayloadHashIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 3);
        String forged = "{\"actorId\":\"admin\"}";
        StoreAccess.tamper(store, 2, e -> Edit.payload(e, forged, LedgerHashing.sha256Hex(forged)));
        assertEquals(VerificationFailure.HASH_MISMATCH, ledger.verify().failure());
    }

    @Test
    void recomputingHashesWithoutTheKeyIsDetectedByTheSignature() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 1);
        StoreAccess.tamper(store, 1, e -> {
            LedgerEntry forged = Edit.eventType(e, "FORGED");
            return Edit.hash(forged, LedgerHashing.computeEntryHash(forged));
        });
        assertEquals(VerificationFailure.INVALID_SIGNATURE, ledger.verify().failure());
    }

    @Test
    void deletedEntryInTheMiddleIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 5);
        StoreAccess.delete(store, e -> e.sequence() == 3);

        LedgerVerificationResult result = ledger.verify();
        assertEquals(VerificationFailure.SEQUENCE_GAP, result.failure());
        assertEquals(3, result.failedAtSequence().longValue());
    }

    @Test
    void renumberedEntryIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 4);
        StoreAccess.delete(store, e -> e.sequence() == 4);
        StoreAccess.tamper(store, 3, e -> Edit.sequence(e, 4));

        LedgerVerificationResult result = ledger.verify();
        assertFalse(result.valid());
        assertEquals(VerificationFailure.SEQUENCE_GAP, result.failure());
    }

    @Test
    void truncationIsOnlyDetectedWithACheckpoint() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 5);
        LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();
        StoreAccess.delete(store, e -> e.sequence() >= 4);

        LedgerVerificationResult withoutCheckpoint = ledger.verify();
        LedgerVerificationResult withCheckpoint = ledger.verify(LedgerVerificationOptions.withCheckpoint(checkpoint));

        assertTrue(withoutCheckpoint.valid()); // documented limit
        assertEquals(3, withoutCheckpoint.lastSequence());
        assertEquals(VerificationFailure.TRUNCATED, withCheckpoint.failure());
    }

    @Test
    void fullRewriteBySomeoneHoldingTheKeyIsDetectedWithACheckpoint() {
        LedgerCheckpoint checkpoint;
        try (ProvanceLedger original = TestLedgers.start(new InMemoryLedgerStore())) {
            for (int i = 0; i < 3; i++) {
                original.addEntry("EVENT", AuditedPayload.of("user", "original"));
            }
            checkpoint = original.createCheckpoint().orElseThrow();
        }

        // The insider rebuilds a different history with the same key in a fresh store.
        ProvanceLedger insider = open(new InMemoryLedgerStore());
        for (int i = 0; i < 3; i++) {
            insider.addEntry("EVENT", AuditedPayload.of(null, "rewritten"));
        }
        assertTrue(insider.verify().valid());

        LedgerVerificationResult result = insider.verify(LedgerVerificationOptions.withCheckpoint(checkpoint));
        assertEquals(VerificationFailure.CHECKPOINT_MISMATCH, result.failure());
    }

    @Test
    void forgedCheckpointIsRejected() {
        ProvanceLedger ledger = withEntries(new InMemoryLedgerStore(), 2);
        LedgerCheckpoint forged = Edit.checkpointSequence(ledger.createCheckpoint().orElseThrow(), 1);

        LedgerVerificationResult result = ledger.verify(LedgerVerificationOptions.withCheckpoint(forged));
        assertEquals(VerificationFailure.CHECKPOINT_MISMATCH, result.failure());
        assertTrue(result.message().contains("signature"), result.message());
    }

    @Test
    void incrementalVerificationStartsAtTheCheckpoint() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 3);
        LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();
        for (int i = 0; i < 2; i++) {
            ledger.addEntry("EVENT", TestLedgers.payload(i));
        }

        LedgerVerificationOptions options = LedgerVerificationOptions.withCheckpoint(checkpoint).startingFromCheckpoint();
        LedgerVerificationResult result = ledger.verify(options);
        assertTrue(result.valid(), result.message());
        assertEquals(3, result.entriesVerified()); // checkpoint entry + 2 new ones
        assertEquals(5, result.lastSequence());

        StoreAccess.tamper(store, 5, e -> Edit.eventType(e, "X"));
        assertEquals(VerificationFailure.HASH_MISMATCH, ledger.verify(options).failure());
    }

    @Test
    void entryFromAnotherLedgerIsDetected() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        try (ProvanceLedger other = TestLedgers.start(store, o -> o.setLedgerId("another-ledger"))) {
            other.addEntry("EVENT", TestLedgers.payload(1));
        }
        LedgerVerifier verifier = new LedgerVerifier(store, new HmacLedgerSigner("k1", TestLedgers.SECRET), TestLedgers.LEDGER_ID);
        assertEquals(VerificationFailure.LEDGER_ID_MISMATCH, verifier.verify().failure());
    }

    @Test
    void redactedPayloadKeepsTheChainValid() {
        ProvanceLedger ledger = withEntries(new InMemoryLedgerStore(), 3);
        LedgerEntry last = ledger.getHead().orElseThrow();
        assertTrue(ledger.redactPayload(last.id()));

        LedgerEntry redacted = ledger.getEntry(last.id()).orElseThrow();
        LedgerVerificationResult result = ledger.verify();

        assertTrue(redacted.isRedacted());
        assertNull(redacted.readPayload());
        assertTrue(result.valid(), result.message());
        assertEquals(1, result.redactedPayloads());
    }

    @Test
    void rotatedKeysVerifyWithRetiredKeysOnly() {
        String newSecret = "second-unit-test-secret-key-abcdef0123456789";
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        try (ProvanceLedger before = TestLedgers.start(store)) {
            before.addEntry("EVENT", AuditedPayload.of(null, "old key"));
        }
        ProvanceLedger after = TestLedgers.start(store, o -> {
            o.setSigningKeyId("k2");
            o.setSecretKey(newSecret);
            o.getRetiredKeys().put("k1", TestLedgers.SECRET);
        });
        opened.add(after);

        LedgerEntry entry = after.addEntry("EVENT", AuditedPayload.of(null, "new key"));
        assertEquals("k2", entry.keyId());
        assertTrue(after.verify().valid());

        LedgerVerifier withoutRetiredKey = new LedgerVerifier(store, new HmacLedgerSigner("k2", newSecret), TestLedgers.LEDGER_ID);
        LedgerVerificationResult result = withoutRetiredKey.verify();
        assertEquals(VerificationFailure.UNKNOWN_KEY, result.failure());
        assertEquals(1, result.failedAtSequence().longValue());
    }

    @Test
    void throwOnFailureRaisesLedgerTamperedException() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        ProvanceLedger ledger = withEntries(store, 2);
        StoreAccess.tamper(store, 1, e -> Edit.eventType(e, "X"));

        LedgerTamperedException ex = assertThrows(LedgerTamperedException.class,
                () -> ledger.verify(LedgerVerificationOptions.defaults().throwingOnFailure()));
        assertEquals(VerificationFailure.HASH_MISMATCH, ex.result().failure());
    }

    @Test
    @SuppressWarnings("unchecked")
    void entriesExposeTheirPayloadAndMillisecondTimestamps() {
        ProvanceLedger ledger = withEntries(new InMemoryLedgerStore(), 1);
        LedgerEntry head = ledger.getHead().orElseThrow();
        Map<String, Object> payload = (Map<String, Object>) head.readPayload();
        assertEquals("user-1", payload.get("actorId"));
        assertEquals(head.timestamp(), LedgerHashing.normalizeTimestamp(head.timestamp()));
    }
}
