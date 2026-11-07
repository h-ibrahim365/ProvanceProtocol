"""The ledger: append audit events, verify the chain, create checkpoints."""

from __future__ import annotations

import asyncio
import logging
import os
import threading
from concurrent.futures import Future
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterable

from ._writer import LedgerWriter, PendingEntry
from .checkpoints import CheckpointPublisher, FileCheckpointSink, LedgerCheckpointSink
from .errors import LedgerUnavailableError
from .format import FORMAT_VERSION, compute_checkpoint_digest, normalize_timestamp, sha256_hex
from .models import LedgerCheckpoint, LedgerEntry, normalize_id
from .options import ProvanceOptions
from .payload import PayloadSerializer, canonical_payload
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
        self._writer = LedgerWriter(store, self.signer, opts, self._clock)
        self._publisher = CheckpointPublisher(self.create_checkpoint, sinks, opts.checkpoint_interval, opts.ledger_id)
        self._writer.start()
        self._publisher.start()

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
               raise_on_failure: bool = False) -> VerificationResult:
        """Verifies the chain. See :meth:`LedgerVerifier.verify`."""
        return self.verifier.verify(checkpoint, start_from_checkpoint, raise_on_failure)

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
        """Removes the payload of an entry while keeping the chain verifiable (GDPR erasure)."""
        return self.store.redact_payload(normalize_id(entry_id))

    def close(self) -> None:
        """Stops accepting entries, writes the queued ones (up to ``shutdown_timeout``) and stops the background
        threads. Entries still queued after the timeout fail with :class:`LedgerUnavailableError`. Idempotent."""
        with self._close_lock:
            if self._closed:
                return
            self._closed = True
        self._publisher.stop()
        self._writer.stop_accepting()
        self._writer.thread.join(self.options.shutdown_timeout.total_seconds())
        if self._writer.thread.is_alive():
            log.warning("PROVANCE writer did not drain its queue before the shutdown timeout; remaining entries fail.")
            self._writer.abort()
            self._writer.fail_queued()
            self._writer.thread.join(5)

    def __enter__(self) -> "ProvanceLedger":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()

    def _prepare(self, event_type: str, payload: Any) -> PendingEntry:
        if not isinstance(event_type, str) or not event_type.strip():
            raise ValueError("Event type is required.")
        if len(event_type) > MAX_EVENT_TYPE_LENGTH:
            raise ValueError(f"Event type is limited to {MAX_EVENT_TYPE_LENGTH} characters.")
        if self._closed:
            raise LedgerUnavailableError("PROVANCE ledger is closed; the entry was not queued.")
        if os.getpid() != self._pid:
            raise LedgerUnavailableError(
                "This PROVANCE ledger was created in a parent process; its writer thread does not exist after fork. "
                "Create the ledger in each worker process (for example in gunicorn's post_fork hook, or lazily on "
                "first use), not before the fork (--preload).")
        payload_json = canonical_payload(payload, self._serializer)
        return PendingEntry(event_type, payload_json, sha256_hex(payload_json))
