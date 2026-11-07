"""Background writer of one process. Same algorithm as the .NET and Java writers.

Linearity across processes comes from the store: it rejects a sequence or id that already exists. On any failure
the writer reloads the head from the store, acknowledges the entries that were in fact persisted (looked up by
their fixed id) and reseals the others on top of the real head. Several application instances can therefore
write to the same ledger.
"""

from __future__ import annotations

import logging
import queue
import random
import threading
import time
import uuid
from concurrent.futures import Future
from dataclasses import dataclass, field
from datetime import datetime
from typing import Callable

from .errors import LedgerConcurrencyError, LedgerOverloadedError, LedgerUnavailableError
from .format import FORMAT_VERSION, MAX_SEQUENCE, compute_entry_hash, normalize_timestamp
from .models import LedgerEntry
from .options import ProvanceOptions, QueueFullBehavior
from .signing import LedgerSigner
from .stores import LedgerStore

log = logging.getLogger("provance.writer")

_POLL_SECONDS = 0.1


@dataclass
class PendingEntry:
    """An entry prepared by a producer (payload canonicalized and hashed) waiting for the writer.

    The id is fixed here so that the writer can recognize the entry in the store after an ambiguous failure.
    """

    event_type: str
    payload_json: str
    payload_hash: str
    id: str = field(default_factory=lambda: str(uuid.uuid4()))
    future: Future = field(default_factory=Future)

    def succeed(self, entry: LedgerEntry) -> None:
        if not self.future.done():
            self.future.set_result(entry)

    def fail(self, error: BaseException) -> None:
        if not self.future.done():
            self.future.set_exception(error)


class _Aborted(Exception):
    pass


class LedgerWriter:
    def __init__(self, store: LedgerStore, signer: LedgerSigner, options: ProvanceOptions,
                 clock: Callable[[], datetime]):
        self._queue: queue.Queue[PendingEntry] = queue.Queue(maxsize=options.queue_capacity)
        self._store = store
        self._signer = signer
        self._options = options
        self._clock = clock
        self._genesis = options.effective_genesis_hash()
        self._accepting = True
        self._accept_lock = threading.Lock()  # an entry is queued only while accepting, never after the writer stopped
        self._aborted = False
        self._head: tuple[int, str] | None = None  # writer thread only
        self.on_appended: Callable[[], None] | None = None  # wakes the archive copy
        self.thread = threading.Thread(target=self._run, name=f"provance-writer-{options.ledger_id}", daemon=True)

    def start(self) -> None:
        self.thread.start()

    def try_enqueue(self, entry: PendingEntry) -> bool:
        """Queues the entry without blocking. Returns ``False`` when the queue is full and the behavior is ``WAIT``.

        :raises LedgerOverloadedError: the queue is full and the behavior is ``FAIL_FAST``
        :raises LedgerUnavailableError: the writer is stopping
        """
        with self._accept_lock:
            if not self._accepting:
                raise _stopped()
            try:
                self._queue.put_nowait(entry)
                return True
            except queue.Full:
                if self._options.queue_full_behavior is QueueFullBehavior.FAIL_FAST:
                    raise LedgerOverloadedError("PROVANCE queue is full.") from None
                return False

    def enqueue(self, entry: PendingEntry) -> None:
        """Queues the entry, waiting for room when the behavior is ``WAIT``. Blocks the calling thread: async code uses
        :meth:`try_enqueue`."""
        while not self.try_enqueue(entry):
            time.sleep(_POLL_SECONDS / 10)

    def stop_accepting(self) -> None:
        with self._accept_lock:
            self._accepting = False

    def abort(self) -> None:
        with self._accept_lock:
            self._accepting = False
        self._aborted = True

    def fail_queued(self) -> None:
        while True:
            try:
                self._queue.get_nowait().fail(LedgerUnavailableError("PROVANCE writer stopped before the entry was written."))
            except queue.Empty:
                return

    def _run(self) -> None:
        log.info("PROVANCE writer started for ledger %s.", self._options.ledger_id)
        try:
            while not self._aborted:
                try:
                    first = self._queue.get(timeout=_POLL_SECONDS)
                except queue.Empty:
                    if not self._accepting and self._queue.empty():
                        break
                    continue
                batch = [first]
                while len(batch) < self._options.max_batch_size:
                    try:
                        batch.append(self._queue.get_nowait())
                    except queue.Empty:
                        break
                try:
                    self._process(batch)
                except _Aborted:
                    for item in batch:
                        item.fail(LedgerUnavailableError("PROVANCE writer stopped before the entry was written."))
                    break
                except Exception as e:  # never let the writer die
                    log.exception("Unexpected error in the PROVANCE writer. The batch is failed and the writer continues.")
                    self._head = None
                    for item in batch:
                        item.fail(LedgerUnavailableError(f"Unexpected PROVANCE writer error: {e}"))
        finally:
            with self._accept_lock:
                self._accepting = False
            self.fail_queued()
            log.info("PROVANCE writer stopped for ledger %s.", self._options.ledger_id)

    def _process(self, batch: list[PendingEntry]) -> None:
        pending = list(batch)
        transient_failures = conflicts = 0

        while pending:
            if self._aborted:
                raise _Aborted()
            sealed: list[LedgerEntry] | None = None
            try:
                head = self._head if self._head is not None else self._load_head()
                sealed = self._seal(pending, head)
                self._store.append(sealed)
                for item, entry in zip(pending, sealed):
                    item.succeed(entry)
                self._head = (sealed[-1].sequence, sealed[-1].hash)
                self._notify()
                return
            except LedgerConcurrencyError as e:
                failure: Exception = e
                is_conflict = True
            except Exception as e:
                failure = e
                is_conflict = False

            if self._aborted:
                raise _Aborted()

            # Whatever happened, our view of the head is no longer trustworthy.
            self._head = None

            if sealed is not None:
                self._acknowledge_persisted(pending)
                if not pending:
                    return

            if is_conflict:
                conflicts += 1
                if conflicts > self._options.max_conflict_retries:
                    log.error("PROVANCE writer gave up after %d sequence conflicts.", conflicts)
                    error = LedgerUnavailableError(f"Gave up after {conflicts} sequence conflicts with other writers.")
                    error.__cause__ = failure
                    for item in pending:
                        item.fail(error)
                    return
                log.debug("Sequence conflict (another writer appended first). Resealing %d entries.", len(pending))
                continue

            transient_failures += 1
            if transient_failures >= self._options.max_append_attempts:
                log.error("PROVANCE append failed %d times; failing %d entries: %s", transient_failures, len(pending), failure)
                error = LedgerUnavailableError(f"The ledger store failed {transient_failures} times: {failure}")
                error.__cause__ = failure
                for item in pending:
                    item.fail(error)
                return

            delay = self._backoff(transient_failures)
            log.warning("PROVANCE append failed (attempt %d/%d), retrying in %.0f ms: %s",
                        transient_failures, self._options.max_append_attempts, delay * 1000, failure)
            time.sleep(delay)

    def _acknowledge_persisted(self, pending: list[PendingEntry]) -> None:
        acknowledged = 0
        try:
            for item in pending:
                stored = self._store.get_by_id(item.id)
                if stored is None:
                    break
                item.succeed(stored)
                acknowledged += 1
        except Exception as e:
            # If an entry was persisted after all, the next append reuses its id, the store reports a conflict and
            # this method runs again.
            log.warning("Could not check which entries were persisted after a failed append: %s", e)
        if acknowledged:
            log.info("%d entries were persisted despite the reported failure.", acknowledged)
            del pending[:acknowledged]
            self._notify()

    def _notify(self) -> None:
        if self.on_appended is not None:
            try:
                self.on_appended()
            except Exception:  # never let a listener break the writer
                log.exception("PROVANCE append listener failed.")

    def _load_head(self) -> tuple[int, str]:
        head = self._store.get_head()
        if head is None:
            return 0, self._genesis
        if head.ledger_id != self._options.ledger_id:
            raise RuntimeError(f"The store contains ledger '{head.ledger_id}' but this application is configured for "
                               f"'{self._options.ledger_id}'. Use one table per ledger.")
        return head.sequence, head.hash

    def _seal(self, pending: list[PendingEntry], head: tuple[int, str]) -> list[LedgerEntry]:
        sequence, previous = head
        timestamp = normalize_timestamp(self._clock())
        key_id = self._signer.key_id
        sealed = []
        for item in pending:
            sequence += 1
            if sequence > MAX_SEQUENCE:
                raise RuntimeError("The ledger reached the maximum sequence number.")
            draft = LedgerEntry(FORMAT_VERSION, self._options.ledger_id, sequence, item.id, timestamp, item.event_type,
                                item.payload_json, item.payload_hash, previous, key_id, "", "")
            digest = compute_entry_hash(draft)
            entry = draft.with_changes(hash=digest, signature=self._signer.sign(bytes.fromhex(digest)).hex())
            sealed.append(entry)
            previous = digest
        return sealed

    def _backoff(self, failures: int) -> float:
        base = self._options.retry_base_delay.total_seconds() * 2 ** (failures - 1)
        return min(base + base * 0.2 * random.random(), 2.0)


def _stopped() -> LedgerUnavailableError:
    return LedgerUnavailableError("PROVANCE ledger is closed; the entry was not queued.")
