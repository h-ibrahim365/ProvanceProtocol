package io.github.hibrahim365.provance;

/** What {@code addEntry} does when the in-memory queue is full. */
public enum QueueFullBehavior {

    /** Block the caller until there is room (backpressure). */
    WAIT,

    /** Throw {@link LedgerOverloadedException} immediately. */
    FAIL_FAST
}
