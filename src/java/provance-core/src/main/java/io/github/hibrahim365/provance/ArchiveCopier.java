package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.compare.LedgerComparer;
import io.github.hibrahim365.provance.store.LedgerStore;

import java.time.Clock;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;
import java.util.logging.Level;
import java.util.logging.Logger;
import java.util.stream.Stream;

/**
 * Copies every entry of the primary store to the archive, in the background. Each entry is checked before it is
 * copied (it must continue the archive's chain and verify on its own), so an entry modified in the primary store
 * before it was copied is not propagated: copying stops and the problem is logged.
 */
final class ArchiveCopier implements Runnable {

    private static final Logger LOG = Logger.getLogger(ArchiveCopier.class.getName());

    final LedgerStore primary;
    final LedgerStore archive;
    private final LedgerComparer checker;
    private final ProvanceOptions options;
    private final Clock clock;
    private final Semaphore wake = new Semaphore(0);
    private final ReentrantLock gate = new ReentrantLock();
    private volatile boolean stopping;
    private volatile ArchiveStatus status = new ArchiveStatus(true, 0, null, null);
    private String loggedProblem;
    private final Thread thread;

    ArchiveCopier(LedgerStore primary, LedgerStore archive, LedgerComparer checker, ProvanceOptions options, Clock clock) {
        this.primary = primary;
        this.archive = archive;
        this.checker = checker;
        this.options = options;
        this.clock = clock;
        this.thread = new Thread(this, "provance-archive-" + options.getLedgerId());
        this.thread.setDaemon(true);
    }

    void start() {
        thread.start();
    }

    ArchiveStatus status() {
        return status;
    }

    /** Wakes the copy loop (called by the writer after each append). */
    void signal() {
        if (wake.availablePermits() == 0) {
            wake.release();
        }
    }

    /** Stops the loop, then copies what the writer wrote last. */
    void stop(long timeoutMillis) {
        stopping = true;
        wake.release();
        try {
            thread.join(Math.max(1, timeoutMillis));
            copy();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (RuntimeException e) {
            LOG.log(Level.WARNING, "PROVANCE could not copy the last entries to the archive before shutdown; they are copied at the next start.", e);
        }
    }

    @Override
    public void run() {
        long interval = options.getArchiveInterval().toMillis();
        while (!stopping) {
            try {
                copy();
            } catch (RuntimeException e) {
                ArchiveStatus s = status;
                status = new ArchiveStatus(s.healthy(), s.archivedThrough(), "Archive copy failed: " + e.getMessage(), s.lastCopyAt());
                LOG.log(Level.WARNING, "PROVANCE archive copy failed for ledger " + options.getLedgerId() + "; it will be retried.", e);
            }
            try {
                if (wake.tryAcquire(interval, TimeUnit.MILLISECONDS)) {
                    wake.drainPermits();
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
        }
    }

    /** Copies every entry the archive does not have yet; returns the last archived sequence. */
    long copy() {
        gate.lock();
        try {
            for (int attempt = 0; ; attempt++) {
                try {
                    return copyOnce();
                } catch (LedgerConcurrencyException e) {
                    if (attempt >= 9) {
                        throw e; // another application instance kept copying the same entries first
                    }
                }
            }
        } finally {
            gate.unlock();
        }
    }

    private long copyOnce() {
        Optional<LedgerEntry> head = archive.getHead();
        if (head.isPresent() && !head.get().ledgerId().equals(options.getLedgerId())) {
            return halt(head.get().sequence(), "The archive contains ledger '" + head.get().ledgerId() + "', not '"
                    + options.getLedgerId() + "'. Use one archive table per ledger.");
        }
        long next = head.map(e -> e.sequence() + 1).orElse(1L);
        String previous = head.map(LedgerEntry::hash).orElse(options.effectiveGenesisHash());
        long archived = head.map(LedgerEntry::sequence).orElse(0L);
        int max = options.getMaxBatchSize();

        while (true) {
            List<LedgerEntry> batch = new ArrayList<>();
            String problem = null;
            try (Stream<LedgerEntry> entries = primary.read(next)) {
                Iterator<LedgerEntry> it = entries.iterator();
                while (it.hasNext()) {
                    LedgerEntry entry = it.next();
                    if (entry.sequence() != next) {
                        problem = entry.sequence() > next
                                ? "Entry " + next + " is missing from the primary store but was never copied to the archive."
                                : "Entry " + entry.sequence() + " appears twice or out of order in the primary store.";
                    } else if (!entry.previousHash().equals(previous)) {
                        problem = "Entry " + entry.sequence() + " of the primary store does not link to the archived entry "
                                + (entry.sequence() - 1) + ": the primary store was modified.";
                    } else if (!checker.verifiesAlone(entry)) {
                        problem = "Entry " + entry.sequence() + " of the primary store does not verify: it was modified before it was copied.";
                    }
                    if (problem != null) {
                        break;
                    }
                    batch.add(entry);
                    previous = entry.hash();
                    next++;
                    if (batch.size() >= max) {
                        break;
                    }
                }
            }
            if (!batch.isEmpty()) {
                archive.append(batch);
                archived = batch.get(batch.size() - 1).sequence();
            }
            if (problem != null) {
                return halt(archived, problem);
            }
            if (batch.size() < max) {
                break;
            }
        }
        loggedProblem = null;
        status = new ArchiveStatus(true, archived, null, clock.instant());
        return archived;
    }

    private long halt(long archived, String problem) {
        if (!problem.equals(loggedProblem)) {
            LOG.log(Level.SEVERE, "PROVANCE stopped copying ledger {0} to the archive: {1}", new Object[] {options.getLedgerId(), problem});
            loggedProblem = problem;
        }
        status = new ArchiveStatus(false, archived, problem, status.lastCopyAt());
        return archived;
    }
}
