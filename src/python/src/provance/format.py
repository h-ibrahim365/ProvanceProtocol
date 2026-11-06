"""The bytes PROVANCE hashes and signs (docs/SPEC.md, format version 1).

Canonical JSON comes from the ``rfc8785`` package, an implementation of RFC 8785 independent of the .NET
and Java libraries. That independence is the point: a ledger that verifies here was not checked by the
code that wrote it.
"""

from __future__ import annotations

import hashlib
import re
from datetime import datetime, timedelta, timezone

import rfc8785

FORMAT_VERSION = 1
"""Version of the signed envelope (field ``v``)."""

MAX_SEQUENCE = 2**53 - 1
"""Largest sequence number. Sequences are JSON numbers and must stay exact as IEEE 754 doubles."""

GENESIS_PREFIX = "provance:genesis:v1:"

_LOWER_HEX = re.compile(r"\A(?:[0-9a-f]{2})+\Z")


def sha256_hex(data: str | bytes) -> str:
    """Lowercase hex SHA-256 of ``data`` (strings are encoded as UTF-8)."""
    if isinstance(data, str):
        data = data.encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def derive_genesis_hash(ledger_id: str) -> str:
    """Default genesis hash of a ledger: SHA-256 of ``"provance:genesis:v1:" + ledger_id``."""
    if not ledger_id or not ledger_id.strip():
        raise ValueError("ledger_id is required")
    return sha256_hex(GENESIS_PREFIX + ledger_id)


def is_lower_hex(value: object, length: int | None = None) -> bool:
    """True if ``value`` is non-empty lowercase hex of even length (and of ``length`` characters when given)."""
    if not isinstance(value, str) or not _LOWER_HEX.match(value):
        return False
    return length is None or len(value) == length


def normalize_timestamp(value: datetime) -> datetime:
    """Converts to UTC and truncates to the millisecond. Naive datetimes are taken as UTC."""
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    value = value.astimezone(timezone.utc)
    return value.replace(microsecond=value.microsecond - value.microsecond % 1000)


def format_timestamp(value: datetime) -> str:
    """Formats a timestamp the way it appears in canonical bodies: ``2026-01-15T09:30:00.123Z``."""
    v = normalize_timestamp(value)
    return v.strftime("%Y-%m-%dT%H:%M:%S.") + f"{v.microsecond // 1000:03d}Z"


_TIMESTAMP = re.compile(
    r"\A\s*(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?\s*(Z|[+-]\d{2}:?\d{2})?\s*\Z",
    re.IGNORECASE,
)


def parse_timestamp(text: str) -> datetime:
    """Parses the timestamp forms written by the stores and checkpoints.

    Accepts ``2026-01-15T09:30:00.123Z``, ``2026-01-15T09:30:00+00:00`` (.NET checkpoints) and
    ``2026-01-15 09:30:00.1234567+00:00`` (Entity Framework Core and Java stores on SQLite), with any number
    of fraction digits. Without an offset the value is taken as UTC.
    """
    m = _TIMESTAMP.match(text)
    if not m:
        raise ValueError(f"Unrecognized timestamp: {text!r}")
    year, month, day, hour, minute, second, fraction, offset = m.groups()
    micro = int((fraction or "0")[:6].ljust(6, "0"))
    tz = timezone.utc
    if offset and offset.upper() != "Z":
        sign = 1 if offset[0] == "+" else -1
        digits = offset[1:].replace(":", "")
        tz = timezone(sign * timedelta(hours=int(digits[:2]), minutes=int(digits[2:])))
    value = datetime(int(year), int(month), int(day), int(hour), int(minute), int(second), micro, tzinfo=tz)
    return normalize_timestamp(value)


def canonical_envelope(entry: "LedgerEntry") -> bytes:  # noqa: F821 (forward reference)
    """Canonical envelope of an entry: every field except ``hash``, ``signature`` and the payload itself."""
    if not 1 <= entry.sequence <= MAX_SEQUENCE:
        raise ValueError(f"Sequence must be between 1 and {MAX_SEQUENCE}.")
    return rfc8785.dumps(
        {
            "v": entry.format_version,
            "ledgerId": entry.ledger_id,
            "sequence": entry.sequence,
            "id": entry.id,
            "timestamp": format_timestamp(entry.timestamp),
            "eventType": entry.event_type,
            "payloadHash": entry.payload_hash,
            "previousHash": entry.previous_hash,
            "keyId": entry.key_id,
        }
    )


def compute_entry_hash(entry: "LedgerEntry") -> str:  # noqa: F821
    """Lowercase hex SHA-256 of the canonical envelope."""
    return hashlib.sha256(canonical_envelope(entry)).hexdigest()


def canonical_checkpoint(checkpoint: "LedgerCheckpoint") -> bytes:  # noqa: F821
    """Canonical body of a checkpoint (every field except ``signature``)."""
    return rfc8785.dumps(
        {
            "type": "checkpoint",
            "v": checkpoint.format_version,
            "ledgerId": checkpoint.ledger_id,
            "sequence": checkpoint.sequence,
            "hash": checkpoint.hash,
            "createdAt": format_timestamp(checkpoint.created_at),
            "keyId": checkpoint.key_id,
        }
    )


def compute_checkpoint_digest(checkpoint: "LedgerCheckpoint") -> str:  # noqa: F821
    """Lowercase hex SHA-256 of the canonical checkpoint body."""
    return hashlib.sha256(canonical_checkpoint(checkpoint)).hexdigest()
