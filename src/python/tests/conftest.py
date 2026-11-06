"""Shared test helpers.

Tests need ledgers to verify. ``seal`` builds valid entries the way the writers do; the shared fixtures in
docs/test-vectors come from the .NET and Java libraries themselves.
"""

from __future__ import annotations

import json
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
import rfc8785

from provance import HmacVerifier, LedgerEntry, compute_entry_hash, derive_genesis_hash, sha256_hex

REPO = Path(__file__).resolve().parents[3]
VECTORS = REPO / "docs" / "test-vectors"
LEDGER_ID = "python-test"
SECRET = "python-test-secret-key-0123456789abcdef"


def seal(count: int, ledger_id: str = LEDGER_ID, secret: str = SECRET, key_id: str = "k1",
         start: list[LedgerEntry] | None = None) -> list[LedgerEntry]:
    """Builds a valid chain of ``count`` entries, optionally continuing ``start``."""
    signer = HmacVerifier({key_id: secret})
    entries = list(start or [])
    previous = entries[-1].hash if entries else derive_genesis_hash(ledger_id)
    t0 = datetime(2026, 11, 6, 12, 0, 0, 123000, tzinfo=timezone.utc)
    for _ in range(count):
        sequence = len(entries) + 1
        payload = rfc8785.dumps({"actorId": f"user-{sequence}", "description": f"#{sequence}"}).decode("utf-8")
        draft = LedgerEntry(1, ledger_id, sequence, str(uuid.uuid4()), t0 + timedelta(seconds=sequence), "EVENT",
                            payload, sha256_hex(payload), previous, key_id, "", "")
        digest = compute_entry_hash(draft)
        entry = draft.with_changes(hash=digest, signature=signer.sign(key_id, bytes.fromhex(digest)).hex())
        entries.append(entry)
        previous = entry.hash
    return entries


@pytest.fixture
def verifier_keys() -> HmacVerifier:
    return HmacVerifier({"k1": SECRET})


@pytest.fixture
def vectors() -> dict:
    return json.loads((VECTORS / "v1.json").read_text(encoding="utf-8"))
