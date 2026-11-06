package io.github.hibrahim365.provance;

import io.github.hibrahim365.provance.checkpoint.LedgerCheckpointSink;

import java.lang.System.Logger.Level;
import java.time.Duration;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Publishes a checkpoint of the head to every sink at the checkpoint interval, when the head has moved.
 */
final class CheckpointPublisher {

    private static final System.Logger LOG = System.getLogger(CheckpointPublisher.class.getName());

    private final LedgerService ledger;
    private final List<LedgerCheckpointSink> sinks;
    private final Duration interval;
    private ScheduledExecutorService scheduler;
    private long lastPublishedSequence;

    CheckpointPublisher(LedgerService ledger, List<LedgerCheckpointSink> sinks, Duration interval) {
        this.ledger = ledger;
        this.sinks = List.copyOf(sinks);
        this.interval = interval;
    }

    void start(String ledgerId) {
        if (sinks.isEmpty()) {
            return;
        }
        scheduler = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "provance-checkpoints-" + ledgerId);
            t.setDaemon(true);
            return t;
        });
        long millis = interval.toMillis();
        scheduler.scheduleWithFixedDelay(this::publishOnce, millis, millis, TimeUnit.MILLISECONDS);
    }

    void stop() {
        if (scheduler == null) {
            return;
        }
        scheduler.shutdown();
        try {
            if (!scheduler.awaitTermination(5, TimeUnit.SECONDS)) {
                scheduler.shutdownNow();
            }
        } catch (InterruptedException e) {
            scheduler.shutdownNow();
            Thread.currentThread().interrupt();
        }
    }

    synchronized void publishOnce() {
        try {
            Optional<LedgerCheckpoint> checkpoint = ledger.createCheckpoint();
            if (checkpoint.isEmpty() || checkpoint.get().sequence() == lastPublishedSequence) {
                return;
            }
            for (LedgerCheckpointSink sink : sinks) {
                sink.publish(checkpoint.get());
            }
            lastPublishedSequence = checkpoint.get().sequence();
            LOG.log(Level.INFO, "PROVANCE checkpoint published at sequence {0}.", checkpoint.get().sequence());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (Exception e) {
            LOG.log(Level.ERROR, "PROVANCE checkpoint publication failed; it will be retried at the next interval.", e);
        }
    }
}
