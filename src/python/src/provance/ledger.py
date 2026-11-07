"""The ledger: append audit events, verify the chain, create checkpoints."""

from __future__ import annotations

import asyncio
import logging
import os
import threading
from concurrent.futures import Future
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterable

from datetime import timedelta

from ._writer import LedgerWriter, PendingEntry
from .archive import ArchiveCopier, ArchiveStatus
from .comparison import LedgerComparer, LedgerComparison
from .checkpoints import CheckpointPublisher, FileCheckpointSink, LedgerCheckpointSink
from .errors import LedgerArchiveError, LedgerUnavailableError
from .format import FORMAT_VERSION, compute_checkpoint_digest, normalize_timestamp, sha256_hex
from .models import LedgerCheckpoint, LedgerEntry, normalize_id
from .options import ProvanceOptions
from .payload import PayloadSerializer, canonical_payload
from .records import REDACTION_EVENT, RETENTION_EVENT, is_reserved, redaction_payload, retention_payload
from .signing import HmacLedgerSigner, LedgerSigner
from .stores import LedgerStore
from .verifier import LedgerVerifier, VerificationResult

log = logging.getLogger("provance")

MAX_EVENT_TYPE_LENGTH = 200
_ASYNC_QUEUE_POLL_SECONDS = 0.01


class ProvanceLedger:
    """A PROVANCE ledger. One instance per ledger and per process; thread safe.

    .. code-block:: python

        ledger = ProvanceLedger(
            ledger_id="orders-api-prod",
            secret_key=os.environ["PROVANCE_SECRET_KEY"],
            store=SqlLedgerStore.from_url("postgresql://app@db/audit"),
        )
        ledger.add_entry("ORDER_REFUNDED", AuditedPayload(actor_id=user_id, description="Refund issued",
                                                          data={"orderId": order_id}))
        result = ledger.verify()
        ledger.close()   # on shutdown: writes the queued entries

    A background thread writes the entries. Call :meth:`close` on shutdown (or use the ledger as a context
    manager) so queued entries are written.

    :raises ValueError: the configuration is invalid, or the HMAC key is missing, too short or a placeholder
    """

    def __init__(
        self,
        options: ProvanceOptions | None = None,
        *,
        store: LedgerStore,
        signer: LedgerSigner | None = None,
        payload_serializer: PayloadSerializer | None = None,
        checkpoint_sinks: Iterable[LedgerCheckpointSink] = (),
        checkpoint_directory: str | Path | None = None,
        clock: Callable[[], datetime] | None = None,
        archive: LedgerStore | None = None,
        **settings: Any,
    ):
        opts = replace(options) if options is not None else ProvanceOptions()
        if settings:
            opts = replace(opts, **settings)  # unknown names raise TypeError
        opts.retired_keys = dict(opts.retired_keys)
        opts.validate()
        if store is None:
            raise ValueError("No PROVANCE store is configured. Pass store=SqlLedgerStore(...), InMemoryLedgerStore() "
                             "or your own store.")

        self.options = opts
        self.store = store
        self.signer: LedgerSigner = signer or HmacLedgerSigner(opts.signing_key_id, opts.secret_key, opts.retired_keys)
        self._serializer = payload_serializer
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self.verifier = LedgerVerifier(store, self.signer, opts.ledger_id, opts.effective_genesis_hash())

        sinks = list(checkpoint_sinks)
        if checkpoint_directory is not None:
            sinks.append(FileCheckpointSink(checkpoint_directory))
        self._closed = False
        self._pid = os.getpid()
        self._close_lock = threading.Lock()
        if archive is not None and archive is store:
            raise ValueError(f"The archive of ledger '{opts.ledger_id}' is the same store as its primary store.")
        self.archive = archive
        self.comparer = LedgerComparer(self.signer, opts.ledger_id)
        self._copier = ArchiveCopier(store, archive, self.comparer, opts) if archive is not None else None
        self._retention_lock = threading.Lock()
        self._retention_stop = threading.Event()
        self._sinks = sinks
        self._writer = LedgerWriter(store, self.signer, opts, self._clock)
        self._publisher = CheckpointPublisher(self.create_checkpoint, sinks, opts.checkpoint_interval, opts.ledger_id)
        if self._copier is not None:
            self._writer.on_appended = self._copier.signal
            self._copier.start()
        self._writer.start()
        self._publisher.start()
        self._retention_thread: threading.Thread | None = None
        if opts.retention_keep_for is not None:
            self._retention_thread = threading.Thread(target=self._run_retention, name=f"provance-retention-{opts.ledger_id}",
                                                      daemon=True)
            self._retention_thread.start()

    @property
    def ledger_id(self) -> str:
        return self.options.ledger_id

    def submit(self, event_type: str, payload: Any) -> Future:
        """Queues an entry and returns a :class:`concurrent.futures.Future` resolved with the sealed entry once it is
        stored. Argument errors are raised immediately.

        :raises ValueError: invalid event type, or a payload that cannot be serialized (``PayloadError``)
        :raises LedgerOverloadedError: the queue is full and ``queue_full_behavior`` is ``FAIL_FAST``
        :raises LedgerUnavailableError: the ledger is closed
        """
        pending = self._prepare(event_type, payload)
        self._writer.enqueue(pending)
        return pending.future

    def add_entry(self, event_type: str, payload: Any, timeout: float | None = None) -> LedgerEntry:
        """Appends an entry and blocks until it is stored. Same errors as :meth:`submit`, plus
        :class:`LedgerUnavailableError` when the store kept failing."""
        return self.submit(event_type, payload).result(timeout=timeout)

    async def add_entry_async(self, event_type: str, payload: Any) -> LedgerEntry:
        """Async version of :meth:`add_entry`, for asyncio applications (FastAPI, Starlette, ...).

        Never blocks the event loop: when the queue is full (behavior ``WAIT``), it awaits until there is room, so a
        timeout around this call (such as the middleware's ``audit_timeout``) also bounds the wait for the queue.
        """
        pending = self._prepare(event_type, payload)
        while not self._writer.try_enqueue(pending):
            await asyncio.sleep(_ASYNC_QUEUE_POLL_SECONDS)
        return await asyncio.wrap_future(pending.future)

    def get_head(self) -> LedgerEntry | None:
        return self.store.get_head()

    def get_entry(self, entry_id: str) -> LedgerEntry | None:
        return self.store.get_by_id(normalize_id(entry_id))

    def verify(self, checkpoint: LedgerCheckpoint | None = None, start_from_checkpoint: bool = False,
               raise_on_failure: bool = False, accept_unrecorded_redactions: bool = False) -> VerificationResult:
        """Verifies the chain. See :meth:`LedgerVerifier.verify`."""
        return self.verifier.verify(checkpoint, start_from_checkpoint, raise_on_failure, accept_unrecorded_redactions)

    def create_checkpoint(self) -> LedgerCheckpoint | None:
        """Signed checkpoint of the current head, or ``None`` when the ledger is empty."""
        head = self.store.get_head()
        if head is None:
            return None
        unsigned = LedgerCheckpoint(FORMAT_VERSION, head.ledger_id, head.sequence, head.hash,
                                    normalize_timestamp(self._clock()), self.signer.key_id, "")
        signature = self.signer.sign(bytes.fromhex(compute_checkpoint_digest(unsigned))).hex()
        return replace(unsigned, signature=signature)

    def redact_payload(self, entry_id: str) -> bool:
        """Removes the payload of an entry while keeping the chain verifiable (GDPR erasure).

        A signed redaction record is appended first, so verification can tell this erasure from a payload deleted
        directly in the database. With an archive, the payload is removed from the archive copy too.

        :returns: ``True`` if the entry exists, in the primary store or in the archive
        :raises LedgerArchiveError: the payload was removed from the primary store but not from the archive
        """
        entry_id = normalize_id(entry_id)
        entry = self.store.get_by_id(entry_id)
        archived = None
        if self.archive is not None:
            try:
                archived = self.archive.get_by_id(entry_id)
            except Exception:
                if entry is None:
                    raise
                log.warning("PROVANCE could not read the archive before erasing entry %s.", entry_id, exc_info=True)
        known = entry or archived
        if known is None:
            return False
        if is_reserved(known.event_type):
            raise ValueError("Entries written by the library cannot be redacted.")

        # A payload already missing was not erased through this method: recording it now would make a deletion done
        # in the database look authorized.
        primary_has = entry is not None and entry.payload_json is not None
        archive_has = archived is not None and archived.payload_json is not None
        if primary_has or archive_has:
            self._add_library_entry(REDACTION_EVENT, redaction_payload(known.sequence, known.id))  # record first
        if primary_has:
            self.store.redact_payload(entry_id)
        if self.archive is not None and (archive_has or archived is None):
            try:
                self.archive.redact_payload(entry_id)  # False: not copied yet; the copy carries the erased payload
            except Exception as e:
                raise LedgerArchiveError(
                    f"The payload of entry {entry_id} was erased in the primary store but not in the archive: {e}") from e
        return True

    def archive_status(self) -> ArchiveStatus | None:
        """State of the copy to the archive, or ``None`` without an archive."""
        return self._copier.status if self._copier is not None else None

    def compare_with_archive(self, max_differences: int = 1000) -> LedgerComparison:
        """Compares the primary store with the archive, field by field, after copying the latest entries."""
        if self._copier is None:
            raise ValueError(f"No archive is configured for ledger '{self.ledger_id}'. Pass archive=... to ProvanceLedger.")
        try:
            self._copier.copy()
        except Exception:
            log.warning("PROVANCE could not copy the latest entries to the archive before comparing.", exc_info=True)
        return self.comparer.compare(self.store, self._copier.archive, max_differences)

    def apply_retention(self, keep_for: timedelta) -> "RetentionResult":
        """Removes entries older than ``keep_for`` from the primary store, keeping the ledger verifiable (see
        docs/RETENTION.md). Nothing is removed if the primary store does not verify, or if the archive does not hold an
        identical copy (unless ``retention_require_archive`` is ``False``).

        :raises NotImplementedError: the store cannot delete entries (no ``delete_before`` method)
        """
        if keep_for <= timedelta(0):
            raise ValueError("keep_for must be positive.")
        if not hasattr(self.store, "delete_before"):
            raise NotImplementedError(f"The store {type(self.store).__name__} cannot delete entries (no delete_before).")
        with self._retention_lock:
            return self._apply_retention(keep_for)

    def _apply_retention(self, keep_for: timedelta) -> "RetentionResult":
        def skip(message: str, first: int = 0) -> RetentionResult:
            return RetentionResult(False, message, 0, first)

        archive = self._copier.archive if self._copier is not None else None
        if archive is None and self.options.retention_require_archive:
            return skip("No archive is configured, so nothing is removed. Pass archive=..., or set "
                        "retention_require_archive=False if you keep a copy another way.")
        verification = self.verifier.verify()
        if not verification.valid:
            return skip(f"The primary store does not verify, so nothing is removed: {verification.message}")

        cutoff = normalize_timestamp(self._clock()) - keep_for
        boundary = None
        first = 0
        for entry in self.store.read(1):
            first = first or entry.sequence
            if entry.timestamp >= cutoff:
                break
            boundary = entry
        if boundary is None or boundary.sequence <= first:
            return skip(f"No entry older than {keep_for} can be removed.", first)

        if archive is not None:
            archived = self._copier.copy()
            if archived < boundary.sequence:
                return skip(f"The archive holds entries up to {archived} only; entries before {boundary.sequence} are "
                            "kept until they are copied.", first)
            mismatch = self._first_mismatch(archive, first, boundary.sequence)
            if mismatch is not None:
                return skip(f"The archive does not hold an identical copy, so nothing is removed: {mismatch}", first)

        record = self._add_library_entry(RETENTION_EVENT, retention_payload(boundary.sequence, boundary.hash))
        removed = self.store.delete_before(boundary.sequence)
        checkpoint = self.create_checkpoint()
        if checkpoint is not None:
            for sink in self._sinks:
                try:
                    sink.publish(checkpoint)
                except Exception:
                    log.exception("PROVANCE could not publish the checkpoint taken after retention.")
        if self._copier is not None:
            self._copier.signal()
        return RetentionResult(True, f"{removed} entries before sequence {boundary.sequence} were removed from the "
                                     "primary store.", removed, boundary.sequence, record, checkpoint)

    def _first_mismatch(self, archive: LedgerStore, first: int, through: int) -> str | None:
        archived = iter(archive.read(first))
        for entry in self.store.read(first):
            if entry.sequence > through:
                break
            copy = next(archived, None)
            if copy is None:
                return f"entry {entry.sequence} is missing from the archive."
            if copy.sequence != entry.sequence:
                return f"the archive has entry {copy.sequence} where entry {entry.sequence} was expected."
            difference = self.comparer.compare_entry(copy, entry)
            if difference is not None and difference.is_tampering:
                return difference.summary
        return None

    def _run_retention(self) -> None:
        keep_for = self.options.retention_keep_for
        interval = self.options.retention_interval.total_seconds()
        if self._retention_stop.wait(min(interval, 60.0)):  # first run soon after start
            return
        while True:
            try:
                result = self.apply_retention(keep_for)
                log.log(logging.INFO if result.applied else logging.DEBUG, "PROVANCE retention on ledger %s: %s",
                        self.ledger_id, result.message)
            except Exception:
                log.exception("PROVANCE retention failed on ledger %s; it is retried at the next interval.", self.ledger_id)
            if self._retention_stop.wait(interval):
                return

    def _add_library_entry(self, event_type: str, payload_json: str) -> LedgerEntry:
        pending = self._check_open(PendingEntry(event_type, payload_json, sha256_hex(payload_json)))
        self._writer.enqueue(pending)
        return pending.future.result()

    def close(self) -> None:
        """Stops accepting entries, writes the queued ones (up to ``shutdown_timeout``) and stops the background
        threads. Entries still queued after the timeout fail with :class:`LedgerUnavailableError`. Idempotent."""
        with self._close_lock:
            if self._closed:
                return
            self._closed = True
        self._retention_stop.set()
        if self._retention_thread is not None:
            self._retention_thread.join(5)
        self._publisher.stop()
        self._writer.stop_accepting()
        self._writer.thread.join(self.options.shutdown_timeout.total_seconds())
        if self._writer.thread.is_alive():
            log.warning("PROVANCE writer did not drain its queue before the shutdown timeout; remaining entries fail.")
            self._writer.abort()
            self._writer.fail_queued()
            self._writer.thread.join(5)
        if self._copier is not None:
            self._copier.stop(self.options.shutdown_timeout.total_seconds())  # after the writer: copies its last entries

    def __enter__(self) -> "ProvanceLedger":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()

    def _prepare(self, event_type: str, payload: Any) -> PendingEntry:
        if not isinstance(event_type, str) or not event_type.strip():
            raise ValueError("Event type is required.")
        if len(event_type) > MAX_EVENT_TYPE_LENGTH:
            raise ValueError(f"Event type is limited to {MAX_EVENT_TYPE_LENGTH} characters.")
        if is_reserved(event_type):
            raise ValueError("Event types starting with 'provance.' are reserved for the library.")
        payload_json = canonical_payload(payload, self._serializer)
        return self._check_open(PendingEntry(event_type, payload_json, sha256_hex(payload_json)))

    def _check_open(self, pending: PendingEntry) -> PendingEntry:
        if self._closed:
            raise LedgerUnavailableError("PROVANCE ledger is closed; the entry was not queued.")
        if os.getpid() != self._pid:
            raise LedgerUnavailableError(
                "This PROVANCE ledger was created in a parent process; its writer thread does not exist after fork. "
                "Create the ledger in each worker process (for example in gunicorn's post_fork hook, or lazily on "
                "first use), not before the fork (--preload).")
        return pending


@dataclass(frozen=True)
class RetentionResult:
    """Result of :meth:`ProvanceLedger.apply_retention`."""

    applied: bool
    message: str
    removed_entries: int = 0
    first_sequence: int = 0
    record: LedgerEntry | None = None
    checkpoint: LedgerCheckpoint | None = None
    """Checkpoint of the head after the removal, already published to the checkpoint sinks."""
