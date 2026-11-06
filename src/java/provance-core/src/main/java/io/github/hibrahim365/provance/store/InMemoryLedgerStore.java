package io.github.hibrahim365.provance.store;

import io.github.hibrahim365.provance.LedgerConcurrencyException;
import io.github.hibrahim365.provance.LedgerEntry;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import java.util.function.Predicate;
import java.util.function.UnaryOperator;
import java.util.stream.Stream;

/**
 * Thread safe in-memory store for tests and demos. Everything is lost when the process stops.
 * It is never used as a silent fallback: choose it explicitly.
 */
public final class InMemoryLedgerStore implements LedgerStore {

    private final Object gate = new Object();
    private final List<LedgerEntry> entries = new ArrayList<>(); // ordered by sequence
    private final Map<UUID, Integer> indexById = new HashMap<>();
    private final Map<Long, Integer> indexBySequence = new HashMap<>();

    /** Number of stored entries. */
    public int size() {
        synchronized (gate) {
            return entries.size();
        }
    }

    @Override
    public Optional<LedgerEntry> getHead() {
        synchronized (gate) {
            return entries.isEmpty() ? Optional.empty() : Optional.of(entries.get(entries.size() - 1));
        }
    }

    @Override
    public void append(List<LedgerEntry> batch) {
        synchronized (gate) {
            // All or nothing: check the whole batch before writing.
            Set<UUID> seenIds = new HashSet<>();
            Long previousSequence = null;
            for (LedgerEntry entry : batch) {
                if (indexBySequence.containsKey(entry.sequence())) {
                    throw new LedgerConcurrencyException("An entry with sequence " + entry.sequence() + " already exists.");
                }
                if (indexById.containsKey(entry.id()) || !seenIds.add(entry.id())) {
                    throw new LedgerConcurrencyException("An entry with id " + entry.id() + " already exists.");
                }
                if (previousSequence != null && entry.sequence() != previousSequence + 1) {
                    throw new IllegalArgumentException("Entries of a batch must have contiguous ascending sequences.");
                }
                previousSequence = entry.sequence();
            }
            for (LedgerEntry entry : batch) {
                insert(entry);
            }
        }
    }

    @Override
    public Optional<LedgerEntry> getById(UUID id) {
        synchronized (gate) {
            Integer i = indexById.get(id);
            return i == null ? Optional.empty() : Optional.of(entries.get(i));
        }
    }

    @Override
    public Stream<LedgerEntry> read(long fromSequence) {
        List<LedgerEntry> snapshot;
        synchronized (gate) {
            snapshot = new ArrayList<>();
            for (LedgerEntry e : entries) {
                if (e.sequence() >= fromSequence) {
                    snapshot.add(e);
                }
            }
        }
        return snapshot.stream();
    }

    @Override
    public boolean redactPayload(UUID id) {
        synchronized (gate) {
            Integer i = indexById.get(id);
            if (i == null) {
                return false;
            }
            entries.set(i, entries.get(i).withPayloadJson(null));
            return true;
        }
    }

    /** Test helper: replaces a stored entry as an attacker with database access would. No check is performed. */
    void tamper(long sequence, UnaryOperator<LedgerEntry> change) {
        synchronized (gate) {
            int i = indexBySequence.get(sequence);
            entries.set(i, change.apply(entries.get(i)));
            reindex();
        }
    }

    /** Test helper: deletes entries as an attacker with database access would. */
    void delete(Predicate<LedgerEntry> predicate) {
        synchronized (gate) {
            entries.removeIf(predicate);
            reindex();
        }
    }

    private void insert(LedgerEntry entry) {
        if (entries.isEmpty() || entries.get(entries.size() - 1).sequence() < entry.sequence()) {
            entries.add(entry);
            indexById.put(entry.id(), entries.size() - 1);
            indexBySequence.put(entry.sequence(), entries.size() - 1);
            return;
        }
        int position = 0;
        while (position < entries.size() && entries.get(position).sequence() <= entry.sequence()) {
            position++;
        }
        entries.add(position, entry);
        reindex();
    }

    private void reindex() {
        indexById.clear();
        indexBySequence.clear();
        for (int i = 0; i < entries.size(); i++) {
            indexById.put(entries.get(i).id(), i);
            indexBySequence.put(entries.get(i).sequence(), i);
        }
    }
}
