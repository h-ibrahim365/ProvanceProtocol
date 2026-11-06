package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.crypto.LedgerHashing;
import io.github.hibrahim365.provance.json.Json;
import io.github.hibrahim365.provance.json.JsonCanonicalizer;
import io.github.hibrahim365.provance.json.SimplePayloadSerializer;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.verification.LedgerVerificationOptions;
import io.github.hibrahim365.provance.verification.LedgerVerificationResult;
import io.github.hibrahim365.provance.verification.LedgerVerifier;

import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * The shared test vectors of {@code docs/test-vectors/v1.json}, also checked by the .NET library and by
 * {@code verify.py}. If these pass, a ledger written by one implementation verifies in the other.
 */
class TestVectorTests {

    private static Map<String, Object> vectors() throws IOException {
        Path path = Path.of("../../../docs/test-vectors/v1.json");
        return Json.parseObject(Files.readString(path, StandardCharsets.UTF_8));
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> map(Object o) {
        return (Map<String, Object>) o;
    }

    private static HmacLedgerSigner signer(Map<String, Object> v) {
        Map<String, Object> hmac = map(v.get("hmac"));
        return new HmacLedgerSigner((String) hmac.get("keyId"), (String) hmac.get("secretUtf8"));
    }

    @Test
    void genesisHashMatches() throws IOException {
        Map<String, Object> genesis = map(vectors().get("genesis"));
        assertEquals(genesis.get("hash"), LedgerHashing.deriveGenesisHash((String) genesis.get("ledgerId")));
    }

    @Test
    @SuppressWarnings("unchecked")
    void entriesMatchAndVerifyAsAChain() throws IOException {
        Map<String, Object> v = vectors();
        HmacLedgerSigner signer = signer(v);
        List<LedgerEntry> chain = new ArrayList<>();

        for (Object item : (List<Object>) v.get("entries")) {
            Map<String, Object> vector = map(item);
            Map<String, Object> in = map(vector.get("input"));
            Map<String, Object> expected = map(vector.get("expected"));
            String name = (String) vector.get("name");

            String canonicalPayload = JsonCanonicalizer.serialize(in.get("payload"), SimplePayloadSerializer.INSTANCE);
            assertEquals(expected.get("canonicalPayload"), canonicalPayload, name);
            String payloadHash = LedgerHashing.sha256Hex(canonicalPayload);
            assertEquals(expected.get("payloadHash"), payloadHash, name);

            LedgerEntry draft = new LedgerEntry(1, (String) in.get("ledgerId"), ((Number) in.get("sequence")).longValue(),
                    UUID.fromString((String) in.get("id")), Instant.parse((String) in.get("timestamp")), (String) in.get("eventType"),
                    canonicalPayload, payloadHash, (String) in.get("previousHash"), (String) in.get("keyId"), "", "");
            assertEquals(expected.get("canonicalEnvelope"), LedgerHashing.canonicalEnvelope(draft), name);

            String hash = LedgerHashing.computeEntryHash(draft);
            assertEquals(expected.get("hash"), hash, name);
            String signature = LedgerHashing.toHex(signer.sign(LedgerHashing.hashToBytes(hash)));
            assertEquals(expected.get("signature"), signature, name);

            chain.add(draft.withHashAndSignature(hash, signature));
        }

        InMemoryLedgerStore store = new InMemoryLedgerStore();
        store.append(chain);
        String ledgerId = (String) map(v.get("genesis")).get("ledgerId");
        LedgerVerificationResult result = new LedgerVerifier(store, signer, ledgerId).verify();
        assertTrue(result.valid(), result.message());
        assertEquals(chain.size(), result.entriesVerified());
    }

    @Test
    void checkpointMatches() throws IOException {
        Map<String, Object> v = vectors();
        Map<String, Object> checkpoint = map(v.get("checkpoint"));
        Map<String, Object> in = map(checkpoint.get("input"));
        Map<String, Object> expected = map(checkpoint.get("expected"));

        LedgerCheckpoint unsigned = new LedgerCheckpoint(1, (String) in.get("ledgerId"), ((Number) in.get("sequence")).longValue(),
                (String) in.get("hash"), Instant.parse((String) in.get("createdAt")), (String) in.get("keyId"), "");
        assertEquals(expected.get("canonicalBody"), LedgerHashing.canonicalCheckpoint(unsigned));
        String digest = LedgerHashing.computeCheckpointHash(unsigned);
        assertEquals(expected.get("digest"), digest);
        String signature = LedgerHashing.toHex(signer(v).sign(LedgerHashing.hashToBytes(digest)));
        assertEquals(expected.get("signature"), signature);

        LedgerCheckpoint signed = unsigned.withSignature(signature);
        InMemoryLedgerStore empty = new InMemoryLedgerStore();
        LedgerVerifier verifier = new LedgerVerifier(empty, signer(v), (String) in.get("ledgerId"));
        assertNull(verifier.checkCheckpoint(signed));
        assertEquals(io.github.hibrahim365.provance.verification.VerificationFailure.TRUNCATED,
                verifier.verify(LedgerVerificationOptions.withCheckpoint(signed)).failure());
    }
}
