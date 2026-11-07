"""What an attacker with write access to the database can and cannot do unnoticed."""

from __future__ import annotations

from datetime import datetime, timezone

import pytest

from provance import (
    HmacVerifier,
    LedgerCheckpoint,
    LedgerTamperedError,
    LedgerVerifier,
    ListSource,
    VerificationFailure,
    compute_checkpoint_digest,
    compute_entry_hash,
    derive_genesis_hash,
    sha256_hex,
)

from conftest import LEDGER_ID, SECRET, seal


def verify(entries, checkpoint=None, **kwargs):
    verifier = LedgerVerifier(ListSource(entries), HmacVerifier({"k1": SECRET}), LEDGER_ID)
    return verifier.verify(checkpoint=checkpoint, **kwargs)


def checkpoint_of(entries, sequence=None):
    head = entries[-1] if sequence is None else entries[sequence - 1]
    unsigned = LedgerCheckpoint(1, LEDGER_ID, head.sequence, head.hash, datetime(2026, 11, 6, tzinfo=timezone.utc), "k1", "")
    signature = HmacVerifier({"k1": SECRET}).sign("k1", bytes.fromhex(compute_checkpoint_digest(unsigned))).hex()
    return LedgerCheckpoint(1, LEDGER_ID, head.sequence, head.hash, unsigned.created_at, "k1", signature)


def test_valid_chain():
    result = verify(seal(10))
    assert result.valid
    assert result.entries_verified == 10
    assert result.last_sequence == 10
    assert seal(1)[0].previous_hash == derive_genesis_hash(LEDGER_ID)


def test_empty_ledger_is_valid():
    result = verify([])
    assert result.valid and result.entries_verified == 0


def test_modified_event_type():
    entries = seal(5)
    entries[2] = entries[2].with_changes(event_type="SOMETHING_ELSE")
    result = verify(entries)
    assert result.failure is VerificationFailure.HASH_MISMATCH
    assert result.failed_at_sequence == 3
    assert result.entries_verified == 2


def test_modified_payload():
    entries = seal(3)
    entries[1] = entries[1].with_changes(payload_json='{"actorId":"admin"}')
    assert verify(entries).failure is VerificationFailure.PAYLOAD_HASH_MISMATCH


def test_payload_rewritten_with_matching_payload_hash():
    entries = seal(3)
    forged = '{"actorId":"admin"}'
    entries[1] = entries[1].with_changes(payload_json=forged, payload_hash=sha256_hex(forged))
    assert verify(entries).failure is VerificationFailure.HASH_MISMATCH


def test_recomputed_hash_without_the_key():
    entries = seal(1)
    forged = entries[0].with_changes(event_type="FORGED")
    entries[0] = forged.with_changes(hash=compute_entry_hash(forged))
    assert verify(entries).failure is VerificationFailure.INVALID_SIGNATURE


def test_deleted_entry_in_the_middle():
    entries = seal(5)
    del entries[2]
    result = verify(entries)
    assert result.failure is VerificationFailure.SEQUENCE_GAP
    assert result.failed_at_sequence == 3


def test_broken_link():
    entries = seal(3)
    entries[1] = entries[1].with_changes(previous_hash="0" * 64)
    assert verify(entries).failure is VerificationFailure.BROKEN_LINK


def test_truncation_is_only_detected_with_a_checkpoint():
    entries = seal(5)
    checkpoint = checkpoint_of(entries)
    truncated = entries[:3]

    assert verify(truncated).valid  # documented limit
    result = verify(truncated, checkpoint)
    assert result.failure is VerificationFailure.TRUNCATED
    assert result.failed_at_sequence == 4


def test_full_rewrite_is_detected_with_a_checkpoint():
    original = seal(3)
    checkpoint = checkpoint_of(original)
    rewritten = seal(3)  # same key, same ledger, different history
    assert verify(rewritten).valid
    assert verify(rewritten, checkpoint).failure is VerificationFailure.CHECKPOINT_MISMATCH


def test_forged_checkpoint_is_rejected():
    entries = seal(2)
    good = checkpoint_of(entries)
    forged = LedgerCheckpoint(1, LEDGER_ID, 1, good.hash, good.created_at, "k1", good.signature)
    result = verify(entries, forged)
    assert result.failure is VerificationFailure.CHECKPOINT_MISMATCH
    assert "signature" in result.message


def test_incremental_verification_from_a_checkpoint():
    entries = seal(3)
    checkpoint = checkpoint_of(entries)
    entries = seal(2, start=entries)

    result = verify(entries, checkpoint, start_from_checkpoint=True)
    assert result.valid, result.message
    assert result.entries_verified == 3
    assert result.last_sequence == 5


def test_entry_from_another_ledger():
    entries = seal(2, ledger_id="another-ledger")
    assert verify(entries).failure is VerificationFailure.LEDGER_ID_MISMATCH


def test_a_payload_erased_without_a_record_is_reported():
    entries = seal(3)
    entries[1] = entries[1].with_changes(payload_json=None)  # UPDATE ... SET payload = NULL
    result = verify(entries)
    assert result.failure is VerificationFailure.UNRECORDED_REDACTION
    assert result.failed_at_sequence == 2
    assert result.failed_entry_id == entries[1].id


def test_payloads_erased_with_0_1_are_accepted_on_request():
    entries = seal(3)
    entries[1] = entries[1].with_changes(payload_json=None)
    result = LedgerVerifier(ListSource(entries), HmacVerifier({"k1": SECRET}), LEDGER_ID).verify(
        accept_unrecorded_redactions=True)
    assert result.valid
    assert result.redacted_payloads == 1


def test_unknown_key():
    entries = seal(2, key_id="k9")
    result = verify(entries)
    assert result.failure is VerificationFailure.UNKNOWN_KEY


def test_raise_on_failure():
    entries = seal(2)
    entries[0] = entries[0].with_changes(event_type="X")
    with pytest.raises(LedgerTamperedError) as info:
        verify(entries, raise_on_failure=True)
    assert info.value.result.failure is VerificationFailure.HASH_MISMATCH


def test_short_hmac_keys_are_refused():
    with pytest.raises(ValueError):
        HmacVerifier({"k1": "too-short"})
