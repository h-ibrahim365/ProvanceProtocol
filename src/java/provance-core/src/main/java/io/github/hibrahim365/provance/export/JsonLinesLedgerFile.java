package io.github.hibrahim365.provance.export;

import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.util.Comparator;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.stream.Stream;

/**
 * A ledger export loaded in memory. Read only: verification and comparison read it like any other store, in file
 * order, so entries that were moved or duplicated in the file are reported.
 */
public final class JsonLinesLedgerFile implements LedgerStore {

    private final List<LedgerEntry> entries;

    public JsonLinesLedgerFile(List<LedgerEntry> entries) {
        this.entries = List.copyOf(entries);
    }

    /** The entries, in file order. */
    public List<LedgerEntry> entries() {
        return entries;
    }

    @Override
    public Optional<LedgerEntry> getHead() {
        return entries.stream().max(Comparator.comparingLong(LedgerEntry::sequence));
    }

    @Override
    public void append(List<LedgerEntry> batch) {
        throw new UnsupportedOperationException("A JSON Lines export is read only.");
    }

    @Override
    public Optional<LedgerEntry> getById(UUID id) {
        return entries.stream().filter(e -> e.id().equals(id)).findFirst();
    }

    @Override
    public Stream<LedgerEntry> read(long fromSequence) {
        return entries.stream().filter(e -> e.sequence() >= fromSequence);
    }

    @Override
    public boolean redactPayload(UUID id) {
        throw new UnsupportedOperationException("A JSON Lines export is read only.");
    }
}
