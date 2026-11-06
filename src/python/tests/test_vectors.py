"""The shared test vectors (docs/test-vectors/v1.json) and the ledger written by the Java library."""

from __future__ import annotations

import json
from datetime import datetime

import rfc8785

from provance import (
    HmacVerifier,
    JsonLinesReader,
    LedgerCheckpoint,
    LedgerEntry,
    LedgerVerifier,
    ListSource,
    VerificationFailure,
    canonical_checkpoint,
    canonical_envelope,
    compute_checkpoint_digest,
    compute_entry_hash,
    derive_genesis_hash,
    sha256_hex,
)

from conftest import VECTORS

JAVA_KEYS = {"k1": "provance-test-vector-key-0123456789abcdef", "k2": "provance-test-vector-key-2-abcdef0123456789"}


def test_genesis(vectors):
    assert derive_genesis_hash(vectors["genesis"]["ledgerId"]) == vectors["genesis"]["hash"]


def test_entries_match_and_verify_as_a_chain(vectors):
    signer = HmacVerifier({vectors["hmac"]["keyId"]: vectors["hmac"]["secretUtf8"]})
    chain = []
    for vector in vectors["entries"]:
        given, expected = vector["input"], vector["expected"]
        payload = rfc8785.dumps(given["payload"]).decode("utf-8")
        assert payload == expected["canonicalPayload"], vector["name"]
        assert sha256_hex(payload) == expected["payloadHash"]

        draft = LedgerEntry(1, given["ledgerId"], given["sequence"], given["id"],
                            datetime.fromisoformat(given["timestamp"].replace("Z", "+00:00")), given["eventType"],
                            payload, sha256_hex(payload), given["previousHash"], given["keyId"], "", "")
        assert canonical_envelope(draft).decode("utf-8") == expected["canonicalEnvelope"], vector["name"]
        digest = compute_entry_hash(draft)
        assert digest == expected["hash"]
        signature = signer.sign(given["keyId"], bytes.fromhex(digest)).hex()
        assert signature == expected["signature"]
        chain.append(draft.with_changes(hash=digest, signature=signature))

    result = LedgerVerifier(ListSource(chain), signer, vectors["genesis"]["ledgerId"]).verify()
    assert result.valid, result.message
    assert result.entries_verified == len(chain)


def test_checkpoint(vectors):
    given, expected = vectors["checkpoint"]["input"], vectors["checkpoint"]["expected"]
    checkpoint = LedgerCheckpoint(1, given["ledgerId"], given["sequence"], given["hash"],
                                  datetime.fromisoformat(given["createdAt"].replace("Z", "+00:00")), given["keyId"], "")
    assert canonical_checkpoint(checkpoint).decode("utf-8") == expected["canonicalBody"]
    assert compute_checkpoint_digest(checkpoint) == expected["digest"]


def test_ledger_written_by_the_java_library_verifies():
    folder = VECTORS / "ledger-java"
    checkpoint = LedgerCheckpoint.from_json((folder / "checkpoint.json").read_text(encoding="utf-8"))
    verifier = LedgerVerifier(JsonLinesReader(folder / "entries.jsonl"), HmacVerifier(JAVA_KEYS), "cross-language-test")

    result = verifier.verify(checkpoint=checkpoint)

    assert result.valid, result.message
    assert result.entries_verified == 6
    assert result.redacted_payloads == 1
    assert result.last_hash == checkpoint.hash


def test_java_ledger_needs_the_retired_key():
    folder = VECTORS / "ledger-java"
    only_new_key = HmacVerifier({"k2": JAVA_KEYS["k2"]})
    result = LedgerVerifier(JsonLinesReader(folder / "entries.jsonl"), only_new_key, "cross-language-test").verify()
    assert result.failure is VerificationFailure.UNKNOWN_KEY
    assert result.failed_at_sequence == 1


def test_editing_one_byte_of_the_java_ledger_is_detected(tmp_path):
    lines = (VECTORS / "ledger-java" / "entries.jsonl").read_text(encoding="utf-8").split("\n")  # not splitlines(): payloads contain U+2028
    entry = json.loads(lines[2])
    entry["eventType"] = "SOMETHING_ELSE"
    lines[2] = json.dumps(entry)
    edited = tmp_path / "edited.jsonl"
    edited.write_text("\n".join(lines), encoding="utf-8")

    result = LedgerVerifier(JsonLinesReader(edited), HmacVerifier(JAVA_KEYS), "cross-language-test").verify()
    assert result.failure is VerificationFailure.HASH_MISMATCH
    assert result.failed_at_sequence == 3


def test_export_round_trips_the_java_ledger(tmp_path):
    from provance import write_json_lines

    source = list(JsonLinesReader(VECTORS / "ledger-java" / "entries.jsonl").read())
    with open(tmp_path / "copy.jsonl", "w", encoding="utf-8", newline="\n") as out:
        assert write_json_lines(source, out) == 6
    text = (tmp_path / "copy.jsonl").read_text(encoding="utf-8")
    assert " " not in text  # escaped, so every tool sees one entry per line
    assert len(text.split("\n")) == 7
    assert list(JsonLinesReader(tmp_path / "copy.jsonl").read()) == source
