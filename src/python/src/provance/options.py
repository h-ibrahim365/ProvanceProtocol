"""Ledger configuration. Same settings, names (in snake_case) and defaults as the .NET and Java libraries."""

from __future__ import annotations

import enum
import re
from dataclasses import dataclass, field
from datetime import timedelta

from .format import derive_genesis_hash, is_lower_hex

_LEDGER_ID = re.compile(r"\A[A-Za-z0-9._:-]{1,128}\Z")


class QueueFullBehavior(enum.Enum):
    WAIT = "wait"
    """Block the caller until there is room (backpressure)."""
    FAIL_FAST = "fail_fast"
    """Raise :class:`~provance.errors.LedgerOverloadedError` immediately."""


@dataclass
class ProvanceOptions:
    ledger_id: str = ""
    """Required. Stable identifier of the ledger, for example ``orders-api-prod``."""

    genesis_hash: str | None = None
    """Optional ``previousHash`` of the first entry. Defaults to the hash derived from the ledger id."""

    signing_key_id: str = "k1"
    """Identifier of the current signing key. Change it whenever you rotate the key."""

    secret_key: str | None = None
    """HMAC secret of the current key, at least 32 bytes. Load it from a secret store or an environment variable.
    Not used when you pass your own signer."""

    retired_keys: dict[str, str] = field(default_factory=dict)
    """Retired keys (key id to secret), used only to verify old entries after a rotation."""

    queue_capacity: int = 10_000
    queue_full_behavior: QueueFullBehavior = QueueFullBehavior.WAIT
    max_batch_size: int = 256
    max_append_attempts: int = 5
    """Attempts for a batch that fails with a transient store error before callers get an error."""

    retry_base_delay: timedelta = timedelta(milliseconds=50)
    """Base delay of the exponential backoff between transient failures, capped at 2 seconds."""

    max_conflict_retries: int = 100
    """Attempts when another writer appended first. Conflicts are resolved by reloading the head, without delay."""

    checkpoint_interval: timedelta = timedelta(minutes=5)
    """Interval between automatic checkpoints. Only used when a checkpoint sink is configured."""

    shutdown_timeout: timedelta = timedelta(seconds=30)
    """How long ``close()`` waits for queued entries to be written."""
    archive_interval: timedelta = timedelta(seconds=5)
    """How often the archive copy looks for entries to copy when no write wakes it up. Only used with an archive."""
    retention_keep_for: timedelta | None = None
    """How long entries stay in the primary store (for example 7 days). ``None``: nothing is removed automatically."""
    retention_interval: timedelta = timedelta(hours=24)
    """Interval between two automatic retention runs."""
    retention_require_archive: bool = True
    """Remove entries only when an archive holds the same entries. Set to ``False`` only if you keep a copy another way."""

    def effective_genesis_hash(self) -> str:
        return self.genesis_hash or derive_genesis_hash(self.ledger_id)

    def validate(self) -> None:
        """Checks every setting and reports all problems at once.

        :raises ValueError: the configuration is invalid
        """
        errors: list[str] = []
        if not self.ledger_id or not self.ledger_id.strip():
            errors.append('ledger_id is required (for example "orders-api-prod").')
        elif not _LEDGER_ID.match(self.ledger_id):
            errors.append("ledger_id may only contain letters, digits and . _ : - (max 128 characters).")
        if self.genesis_hash and not is_lower_hex(self.genesis_hash, 64):
            errors.append("genesis_hash must be 64 lowercase hex characters.")
        if not self.signing_key_id or not self.signing_key_id.strip():
            errors.append("signing_key_id is required.")
        if self.queue_capacity < 1:
            errors.append("queue_capacity must be at least 1.")
        if not isinstance(self.queue_full_behavior, QueueFullBehavior):
            errors.append("queue_full_behavior must be a QueueFullBehavior.")
        if not 1 <= self.max_batch_size <= 10_000:
            errors.append("max_batch_size must be between 1 and 10000.")
        if self.max_append_attempts < 1:
            errors.append("max_append_attempts must be at least 1.")
        if self.max_conflict_retries < 0:
            errors.append("max_conflict_retries cannot be negative.")
        if self.retry_base_delay < timedelta(0):
            errors.append("retry_base_delay cannot be negative.")
        if self.checkpoint_interval <= timedelta(0):
            errors.append("checkpoint_interval must be positive.")
        if self.shutdown_timeout < timedelta(0):
            errors.append("shutdown_timeout cannot be negative.")
        if self.archive_interval <= timedelta(0):
            errors.append("archive_interval must be positive.")
        if self.retention_keep_for is not None and self.retention_keep_for <= timedelta(0):
            errors.append("retention_keep_for must be positive.")
        if self.retention_interval <= timedelta(0):
            errors.append("retention_interval must be positive.")
        if errors:
            raise ValueError("PROVANCE configuration is invalid: " + " ".join(errors))
