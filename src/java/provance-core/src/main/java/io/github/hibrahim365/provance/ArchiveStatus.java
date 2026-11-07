package io.github.hibrahim365.provance;

import java.time.Instant;

/**
 * State of the copy to the archive.
 *
 * @param healthy         {@code false} when copying stopped because the primary store does not continue the archive
 * @param archivedThrough last sequence known to be in the archive
 * @param problem         why copying stopped, or the last transient error; {@code null} when everything is fine
 * @param lastCopyAt      time of the last successful copy pass
 */
public record ArchiveStatus(boolean healthy, long archivedThrough, String problem, Instant lastCopyAt) {
}
