package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.json.JsonFormatException;
import io.github.hibrahim365.provance.signing.HmacLedgerSigner;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;

import org.junit.jupiter.api.Test;

import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** The library refuses to start with a configuration that would make the audit trail worthless. */
class ConfigurationTests {

    private static IllegalStateException buildFails(java.util.function.Consumer<ProvanceOptions> configure) {
        return assertThrows(IllegalStateException.class,
                () -> TestLedgers.builder(new InMemoryLedgerStore(), configure).build().close());
    }

    @Test
    void missingLedgerIdIsRejected() {
        IllegalStateException ex = buildFails(o -> o.setLedgerId(""));
        assertTrue(ex.getMessage().contains("ledgerId"), ex.getMessage());
    }

    @Test
    void invalidLedgerIdIsRejected() {
        for (String ledgerId : new String[] {"has space", "slash/not/allowed"}) {
            IllegalStateException ex = buildFails(o -> o.setLedgerId(ledgerId));
            assertTrue(ex.getMessage().contains("ledgerId"), ex.getMessage());
        }
    }

    @Test
    void uppercaseOrShortGenesisHashIsRejected() {
        buildFails(o -> o.setGenesisHash("ABCDEF"));
        buildFails(o -> o.setGenesisHash("A".repeat(64)));
    }

    @Test
    void weakOrPlaceholderSecretIsRejected() {
        for (String secret : new String[] {null, "", "too-short", "DEFAULT_INSECURE_KEY_CHANGE_ME"}) {
            buildFails(o -> o.setSecretKey(secret));
        }
    }

    @Test
    void signerCannotReuseTheSigningKeyIdAsARetiredKey() {
        assertThrows(IllegalStateException.class,
                () -> new HmacLedgerSigner("k1", TestLedgers.SECRET, Map.of("k1", TestLedgers.SECRET)));
    }

    @Test
    void noStoreConfiguredFailsWithAnExplicitMessage() {
        IllegalStateException ex = assertThrows(IllegalStateException.class, () -> ProvanceLedger.builder()
                .ledgerId(TestLedgers.LEDGER_ID)
                .secretKey(TestLedgers.SECRET)
                .build());
        assertTrue(ex.getMessage().contains("No PROVANCE store is configured"), ex.getMessage());
    }

    @Test
    void explicitGenesisHashIsUsedForTheFirstEntry() {
        String genesis = "ab".repeat(32);
        try (ProvanceLedger ledger = TestLedgers.start(new InMemoryLedgerStore(), o -> o.setGenesisHash(genesis))) {
            assertEquals(genesis, ledger.addEntry("EVENT", TestLedgers.payload(1)).previousHash());
            assertTrue(ledger.verify().valid());
        }
    }

    @Test
    void eventTypeIsValidated() {
        try (ProvanceLedger ledger = TestLedgers.start()) {
            assertThrows(IllegalArgumentException.class, () -> ledger.addEntry(" ", TestLedgers.payload(1)));
            assertThrows(IllegalArgumentException.class, () -> ledger.addEntry("X".repeat(201), TestLedgers.payload(1)));
            assertThrows(IllegalArgumentException.class, () -> ledger.addEntry("EVENT", null));
        }
    }

    /** A class without getters cannot be serialized by the default serializer. */
    static final class Opaque {
        @SuppressWarnings("unused")
        private final int value = 1;
    }

    @Test
    void unserializablePayloadIsRejectedBeforeQueueing() {
        InMemoryLedgerStore store = new InMemoryLedgerStore();
        try (ProvanceLedger ledger = TestLedgers.start(store)) {
            assertThrows(JsonFormatException.class, () -> ledger.addEntry("EVENT", new Opaque()));
            assertThrows(JsonFormatException.class, () -> ledger.addEntry("EVENT", Map.of("n", Double.NaN)));
            assertEquals(0, store.size());
        }
    }

    @Test
    void optionsAreCopiedWhenTheLedgerIsBuilt() {
        ProvanceOptions options = new ProvanceOptions();
        options.setLedgerId(TestLedgers.LEDGER_ID);
        options.setSecretKey(TestLedgers.SECRET);
        try (ProvanceLedger ledger = ProvanceLedger.builder().options(options).store(new InMemoryLedgerStore()).build()) {
            options.setLedgerId("changed-afterwards");
            assertEquals(TestLedgers.LEDGER_ID, ledger.ledgerId());
            assertEquals(TestLedgers.LEDGER_ID, ledger.addEntry("EVENT", TestLedgers.payload(1)).ledgerId());
        }
    }
}
