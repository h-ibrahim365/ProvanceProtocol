"""Exceptions raised by the ledger, the writer and the stores."""

from __future__ import annotations


class LedgerError(Exception):
    """Base class of PROVANCE errors."""


class LedgerConcurrencyError(LedgerError):
    """Raised by a store when an entry with the same sequence or id already exists.

    The writer handles it by reloading the head; applications rarely see it.
    """


class LedgerStoreError(LedgerError):
    """A store operation failed (database unavailable, timeout, ...). The writer retries appends that fail this way."""


class LedgerUnavailableError(LedgerError):
    """The entry could not be written: the ledger is closed, or the store kept failing after all retries.

    When the cause is a store failure, the entry may or may not have been persisted.
    """


class LedgerOverloadedError(LedgerUnavailableError):
    """The queue is full and ``queue_full_behavior`` is ``FAIL_FAST``. Map it to HTTP 503 or 429."""


class PayloadError(ValueError):
    """The payload cannot be serialized to canonical JSON."""
