"""Entries the library writes itself (docs/SPEC.md, sections 6 and 9). Event types starting with ``provance.`` are
reserved for them: applications cannot write such entries."""

from __future__ import annotations

import json
import uuid

from .format import is_lower_hex
from .payload import canonical_payload

RESERVED_PREFIX = "provance."
RETENTION_EVENT = "provance.retention"
REDACTION_EVENT = "provance.redaction"


def is_reserved(event_type: str) -> bool:
    return event_type.lower().startswith(RESERVED_PREFIX)


def retention_payload(first_sequence: int, first_hash: str) -> str:
    if first_sequence < 2:
        raise ValueError("Retention keeps entries from sequence 2 or later.")
    if not is_lower_hex(first_hash, 64):
        raise ValueError("Hash must be 64 lowercase hex characters.")
    return canonical_payload({"firstHash": first_hash, "firstSequence": first_sequence})


def parse_retention(payload_json: str | None) -> tuple[int, str] | None:
    data = _object(payload_json)
    if data is None:
        return None
    sequence, first_hash = data.get("firstSequence"), data.get("firstHash")
    if type(sequence) is not int or sequence < 2 or not isinstance(first_hash, str) or not is_lower_hex(first_hash, 64):
        return None
    return sequence, first_hash


def redaction_payload(sequence: int, entry_id: str) -> str:
    if sequence < 1:
        raise ValueError("Sequence must be positive.")
    return canonical_payload({"entryId": str(uuid.UUID(entry_id)), "sequence": sequence})


def parse_redaction(payload_json: str | None) -> tuple[int, str] | None:
    data = _object(payload_json)
    if data is None:
        return None
    sequence, entry_id = data.get("sequence"), data.get("entryId")
    if type(sequence) is not int or sequence < 1 or not isinstance(entry_id, str):
        return None
    try:
        return sequence, str(uuid.UUID(entry_id))
    except ValueError:
        return None


def _object(payload_json: str | None) -> dict | None:
    if payload_json is None:
        return None
    try:
        data = json.loads(payload_json)
    except ValueError:
        return None
    return data if isinstance(data, dict) else None
