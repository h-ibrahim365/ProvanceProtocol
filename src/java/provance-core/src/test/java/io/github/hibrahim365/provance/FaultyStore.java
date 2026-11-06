package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.store.InMemoryLedgerStore;
import io.github.hibrahim365.provance.store.LedgerStore;
import io.github.hibrahim365.provance.store.LedgerStoreException;

import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.IntFunction;
import java.util.function.IntPredicate;
import java.util.stream.Stream;

/** Wraps an {@link InMemoryLedgerStore} and injects failures. */
final class FaultyStore implements LedgerStore {

    enum Fault {
        NONE,
        /** Throws before anything is written. */
        FAIL_BEFORE,
        /** Writes the whole batch, then throws (lost acknowledgement, the classic network timeout). */
        FAIL_AFTER_PERSIST,
        /** Writes only the first entry of the batch, then throws. */
        PERSIST_FIRST_THEN_FAIL
    }

    /** Runs at the start of every append; use it to block the writer. */
    interface Gate {
        void await() throws InterruptedException;
    }

    final InMemoryLedgerStore inner = new InMemoryLedgerStore();
    private final AtomicInteger appendCalls = new AtomicInteger();
    private final AtomicInteger getByIdCalls = new AtomicInteger();
    volatile IntFunction<Fault> onAppend = call -> Fault.NONE;
    volatile IntPredicate failGetById = call -> false;
    volatile Gate appendGate;

    int appendCalls() {
        return appendCalls.get();
    }

    @Override
    public Optional<LedgerEntry> getHead() {
        return inner.getHead();
    }

    @Override
    public void append(List<LedgerEntry> entries) {
        int call = appendCalls.getAndIncrement();
        Gate gate = appendGate;
        if (gate != null) {
            try {
                gate.await();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new LedgerStoreException("Interrupted", e);
            }
        }
        switch (onAppend.apply(call)) {
            case FAIL_BEFORE:
                throw new LedgerStoreException("Simulated store timeout (nothing written).");
            case FAIL_AFTER_PERSIST:
                inner.append(entries);
                throw new LedgerStoreException("Simulated store timeout (batch was written).");
            case PERSIST_FIRST_THEN_FAIL:
                inner.append(List.of(entries.get(0)));
                throw new LedgerStoreException("Simulated failure after the first entry.");
            default:
                inner.append(entries);
        }
    }

    @Override
    public Optional<LedgerEntry> getById(UUID id) {
        if (failGetById.test(getByIdCalls.getAndIncrement())) {
            throw new LedgerStoreException("Simulated read timeout.");
        }
        return inner.getById(id);
    }

    @Override
    public Stream<LedgerEntry> read(long fromSequence) {
        return inner.read(fromSequence);
    }

    @Override
    public boolean redactPayload(UUID id) {
        return inner.redactPayload(id);
    }
}
