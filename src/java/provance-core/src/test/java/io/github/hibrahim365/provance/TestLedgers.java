package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.time.Duration;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;

/** One "application instance" for tests. */
final class TestLedgers {

    static final String LEDGER_ID = "test-ledger";
    static final String SECRET = "unit-test-secret-key-0123456789abcdef";

    private TestLedgers() {
    }

    static ProvanceLedger start() {
        return start(new InMemoryLedgerStore(), o -> { });
    }

    static ProvanceLedger start(LedgerStore store) {
        return start(store, o -> { });
    }

    static ProvanceLedger start(LedgerStore store, Consumer<ProvanceOptions> configure) {
        return builder(store, configure).build();
    }

    static ProvanceLedger.Builder builder(LedgerStore store, Consumer<ProvanceOptions> configure) {
        return ProvanceLedger.builder()
                .configure(o -> {
                    o.setLedgerId(LEDGER_ID);
                    o.setSecretKey(SECRET);
                    o.setRetryBaseDelay(Duration.ofMillis(1));
                    o.setShutdownTimeout(Duration.ofSeconds(10));
                    configure.accept(o);
                })
                .store(store);
    }

    static AuditedPayload payload(int i) {
        return new AuditedPayload(null, null, "#" + i, null);
    }

    static void waitUntil(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not reached within 10 seconds.");
            }
            Thread.sleep(5);
        }
    }
}
