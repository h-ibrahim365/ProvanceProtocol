"""Ledger entries and checkpoints."""

from __future__ import annotations

import json
import uuid
from dataclasses import dataclass, replace
from datetime import datetime
from typing import Any

from .format import format_timestamp, normalize_timestamp, parse_timestamp


def normalize_id(value: object) -> str:
    """Returns an entry id as a lowercase UUID string (stores return ``UUID`` objects or upper case text)."""
    if isinstance(value, uuid.UUID):
        return str(value)
    if isinstance(value, (bytes, bytearray)) and len(value) == 16:
        raise ValueError("Binary UUID columns (Oracle RAW(16)) are not supported; read the id as text.")
    return str(uuid.UUID(str(value).strip()))


@dataclass(frozen=True)
class LedgerEntry:
    """A sealed ledger entry. ``payload_json`` is ``None`` when the payload was redacted."""

    format_version: int
    ledger_id: str
    sequence: int
    id: str
    timestamp: datetime
    event_type: str
    payload_json: str | None
    payload_hash: str
    previous_hash: str
    key_id: str
    hash: str
    signature: str

    @property
    def is_redacted(self) -> bool:
        return self.payload_json is None

    def payload(self) -> Any:
        """The payload parsed with :func:`json.loads`, or ``None`` when redacted."""
        return None if self.payload_json is None else json.loads(self.payload_json)

    def with_changes(self, **changes: Any) -> "LedgerEntry":
        return replace(self, **changes)

    # Export format: one JSON object per line (JSON Lines), field names as in the .NET and Java libraries.

    def to_export(self) -> dict[str, Any]:
        return {
            "v": self.format_version,
            "ledgerId": self.ledger_id,
            "sequence": self.sequence,
            "id": self.id,
            "timestamp": format_timestamp(self.timestamp),
            "eventType": self.event_type,
            "payloadJson": self.payload_json,
            "payloadHash": self.payload_hash,
            "previousHash": self.previous_hash,
            "keyId": self.key_id,
            "hash": self.hash,
            "signature": self.signature,
        }

    @classmethod
    def from_export(cls, data: dict[str, Any]) -> "LedgerEntry":
        try:
            return cls(
                format_version=int(data["v"]),
                ledger_id=str(data["ledgerId"]),
                sequence=int(data["sequence"]),
                id=normalize_id(data["id"]),
                timestamp=parse_timestamp(str(data["timestamp"])),
                event_type=str(data["eventType"]),
                payload_json=data.get("payloadJson"),
                payload_hash=str(data["payloadHash"]),
                previous_hash=str(data["previousHash"]),
                key_id=str(data["keyId"]),
                hash=str(data["hash"]),
                signature=str(data["signature"]),
            )
        except KeyError as e:
            raise ValueError(f"Exported entry has no '{e.args[0]}' field.") from None


@dataclass(frozen=True)
class LedgerCheckpoint:
    """A signed statement "the ledger head was at ``sequence`` with ``hash``".

    The JSON form is the one written by the .NET and Java libraries (``LedgerCheckpoint.ToJson`` /
    ``toJson``) and by their file checkpoint sinks.
    """

    format_version: int
    ledger_id: str
    sequence: int
    hash: str
    created_at: datetime
    key_id: str
    signature: str

    def to_json(self) -> str:
        return json.dumps(
            {
                "v": self.format_version,
                "ledgerId": self.ledger_id,
                "sequence": self.sequence,
                "hash": self.hash,
                "createdAt": format_timestamp(self.created_at),
                "keyId": self.key_id,
                "signature": self.signature,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )

    @classmethod
    def from_json(cls, text: str) -> "LedgerCheckpoint":
        data = json.loads(text)
        if not isinstance(data, dict):
            raise ValueError("Checkpoint JSON must be an object.")
        try:
            return cls(
                format_version=int(data["v"]),
                ledger_id=str(data["ledgerId"]),
                sequence=int(data["sequence"]),
                hash=str(data["hash"]),
                created_at=parse_timestamp(str(data["createdAt"])),
                key_id=str(data["keyId"]),
                signature=str(data["signature"]),
            )
        except KeyError as e:
            raise ValueError(f"Checkpoint JSON has no '{e.args[0]}' field.") from None

    def with_created_at(self, value: datetime) -> "LedgerCheckpoint":
        return replace(self, created_at=normalize_timestamp(value))


@dataclass(frozen=True)
class AuditedPayload:
    """A general purpose payload: who did what, with optional data. Any serializable object can be a payload;
    this one serializes to the same JSON as ``AuditedPayload`` in .NET and Java (camelCase, ``None`` omitted).

    Keep personal data out of payloads when you can: the ledger is append only.
    """

    actor_id: str | None = None
    description: str | None = None
    data: dict[str, Any] | None = None
    actor_role: str | None = None

    def __provance_payload__(self) -> dict[str, Any]:
        result: dict[str, Any] = {}
        if self.actor_id is not None:
            result["actorId"] = self.actor_id
        if self.actor_role is not None:
            result["actorRole"] = self.actor_role
        if self.description is not None:
            result["description"] = self.description
        if self.data is not None:
            result["data"] = self.data
        return result

