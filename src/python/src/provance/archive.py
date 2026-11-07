"""Archive copy: every entry of the primary store is also copied, in the background, to a second store.

Each entry is checked before it is copied (it must continue the archive's chain and verify on its own), so an entry
modified in the primary store before it was copied is not propagated: copying stops and the problem is logged.
Same behavior as the .NET ``ArchiveCopier``.
"""

from __future__ import annotations

import logging
import threading
from dataclasses import dataclass
from datetime import datetime, timezone

from .comparison import LedgerComparer
from .errors import LedgerConcurrencyError
from .models import LedgerEntry
from .options import ProvanceOptions
from .stores import LedgerStore

log = logging.getLogger("provance.archive")


@dataclass(frozen=True)
class ArchiveStatus:
    healthy: bool
    """``False`` when copying stopped because the primary store does not continue the archive."""
    archived_through: int = 0
    """Last sequence known to be in the archive."""
    problem: str | None = None
    last_copy_at: datetime | None = None


class ArchiveCopier:
    def __init__(self, primary: LedgerStore, archive: LedgerStore, checker: LedgerComparer, options: ProvanceOptions):
        self.primary = primary
        self.archive = archive
        self._checker = checker
        self._options = options
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._gate = threading.Lock()
        self._logged_problem: str | None = None
        self.status = ArchiveStatus(True)
        self._thread = threading.Thread(target=self._run, name=f"provance-archive-{options.ledger_id}", daemon=True)

    def start(self) -> None:
        self._thread.start()

    def signal(self) -> None:
        """Wakes the copy loop (called by the writer after each append)."""
        self._wake.set()

    def stop(self, timeout: float) -> None:
        """Stops the loop, then copies what the writer wrote last."""
        self._stop.set()
        self._wake.set()
        self._thread.join(timeout)
        try:
            self.copy()
        except Exception:
            log.warning("PROVANCE could not copy the last entries of ledger %s to the archive before shutdown; they are "
                        "copied at the next start.", self._options.ledger_id, exc_info=True)

    def _run(self) -> None:
        interval = self._options.archive_interval.total_seconds()
        while not self._stop.is_set():
            try:
                self.copy()
            except Exception as e:
                self.status = ArchiveStatus(self.status.healthy, self.status.archived_through,
                                            f"Archive copy failed: {e}", self.status.last_copy_at)
                log.warning("PROVANCE archive copy failed for ledger %s; it will be retried.", self._options.ledger_id,
                            exc_info=True)
            self._wake.wait(interval)
            self._wake.clear()

    def copy(self) -> int:
        """Copies every entry the archive does not have yet. Returns the last archived sequence."""
        with self._gate:
            for attempt in range(10):
                try:
                    return self._copy_once()
                except LedgerConcurrencyError:
                    if attempt == 9:
                        raise  # another application instance kept copying the same entries first
            return self.status.archived_through

    def _copy_once(self) -> int:
        options = self._options
        head = self.archive.get_head()
        if head is not None and head.ledger_id != options.ledger_id:
            return self._halt(head.sequence, f"The archive contains ledger '{head.ledger_id}', not '{options.ledger_id}'. "
                                             "Use one archive table per ledger.")
        next_sequence = head.sequence + 1 if head else 1
        previous = head.hash if head else options.effective_genesis_hash()
        archived = head.sequence if head else 0

        while True:
            batch: list[LedgerEntry] = []
            problem: str | None = None
            for entry in self.primary.read(next_sequence):
                if entry.sequence != next_sequence:
                    problem = (f"Entry {next_sequence} is missing from the primary store but was never copied to the archive."
                               if entry.sequence > next_sequence
                               else f"Entry {entry.sequence} appears twice or out of order in the primary store.")
                elif entry.previous_hash != previous:
                    problem = (f"Entry {entry.sequence} of the primary store does not link to the archived entry "
                               f"{entry.sequence - 1}: the primary store was modified.")
                elif not self._checker.verifies_alone(entry):
                    problem = f"Entry {entry.sequence} of the primary store does not verify: it was modified before it was copied."
                if problem is not None:
                    break
                batch.append(entry)
                previous = entry.hash
                next_sequence += 1
                if len(batch) >= options.max_batch_size:
                    break

            if batch:
                self.archive.append(batch)
                archived = batch[-1].sequence
            if problem is not None:
                return self._halt(archived, problem)
            if len(batch) < options.max_batch_size:
                break

        self._logged_problem = None
        self.status = ArchiveStatus(True, archived, None, datetime.now(timezone.utc))
        return archived

    def _halt(self, archived: int, problem: str) -> int:
        if self._logged_problem != problem:
            log.critical("PROVANCE stopped copying ledger %s to the archive: %s", self._options.ledger_id, problem)
            self._logged_problem = problem
        self.status = ArchiveStatus(False, archived, problem, self.status.last_copy_at)
        return archived
