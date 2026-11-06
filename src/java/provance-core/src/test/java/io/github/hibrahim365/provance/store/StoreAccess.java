package io.github.hibrahim365.provance.store;

import io.github.hibrahim365.provance.LedgerEntry;

import java.util.function.Predicate;
import java.util.function.UnaryOperator;

/** Gives tests the attacker powers of {@link InMemoryLedgerStore} (direct edits of stored rows). */
public final class StoreAccess {

    private StoreAccess() {
    }

    public static void tamper(InMemoryLedgerStore store, long sequence, UnaryOperator<LedgerEntry> change) {
        store.tamper(sequence, change);
    }

    public static void delete(InMemoryLedgerStore store, Predicate<LedgerEntry> predicate) {
        store.delete(predicate);
    }
}
