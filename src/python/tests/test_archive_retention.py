"""0.2: archive copy, field-level comparison, recorded erasures and retention. Same scenarios as the .NET suite."""

from __future__ import annotations

import time
from datetime import datetime, timedelta, timezone

import pytest

from provance import (
    REDACTION_EVENT,
    RETENTION_EVENT,
    AuditedPayload,
    DifferenceKind,
    HmacVerifier,
    InMemoryLedgerStore,
    LedgerArchiveError,
    LedgerVerifier,
    OriginalCopy,
    VerificationFailure,
)
from provance.format import compute_entry_hash, sha256_hex

from conftest import LEDGER_ID, SECRET
from test_ledger import open_ledger


class Clock:
    def __init__(self) -> None:
        self.now = datetime(2026, 11, 1, 8, 0, tzinfo=timezone.utc)

    def __call__(self) -> datetime:
        return self.now


def refunds(ledger, count: int):
    return [ledger.add_entry("REFUND", AuditedPayload(actor_id=f"support-{i}", data={"orderId": f"A-{1000 + i}", "amount": "49.90"}))
            for i in range(1, count + 1)]


def archive_verifier(archive):
    return LedgerVerifier(archive, HmacVerifier({"k1": SECRET}), LEDGER_ID)


# Archive copy and comparison

def test_entries_are_copied_to_the_archive_and_verify_there():
    archive = InMemoryLedgerStore()
    with open_ledger(archive=archive) as ledger:
        refunds(ledger, 5)
        comparison = ledger.compare_with_archive()
        assert comparison.identical, comparison.describe()
        assert comparison.compared == 5
        assert ledger.archive_status().healthy
        assert ledger.archive_status().archived_through == 5
    assert archive_verifier(archive).verify().valid


def test_the_last_entries_are_copied_on_close():
    archive = InMemoryLedgerStore()
    ledger = open_ledger(archive=archive, archive_interval=timedelta(hours=1))
    for i in range(30):
        ledger.submit("EVENT", {"n": i})
    ledger.close()
    assert len(archive) == 30


def test_a_changed_value_shows_its_original_from_the_archive():
    store, archive = InMemoryLedgerStore(), InMemoryLedgerStore()
    with open_ledger(store, archive=archive) as ledger:
        refunds(ledger, 4)
        ledger.compare_with_archive()
        store.tamper(2, lambda e: e.with_changes(payload_json=e.payload_json.replace('"49.90"', '"4990.00"')))

        assert ledger.verify().failure is VerificationFailure.PAYLOAD_HASH_MISMATCH
        comparison = ledger.compare_with_archive()
        [difference] = comparison.differences
        assert difference.sequence == 2
        assert difference.kind is DifferenceKind.CHANGED
        assert difference.original is OriginalCopy.ARCHIVE
        [field] = difference.fields
        assert (field.field, field.archive_value, field.primary_value) == ("payload.data.amount", '"49.90"', '"4990.00"')
        assert 'payload.data.amount: "49.90" -> "4990.00"' in comparison.describe()


def test_deleted_entries_and_a_change_in_the_archive_are_attributed():
    store, archive = InMemoryLedgerStore(), InMemoryLedgerStore()
    with open_ledger(store, archive=archive) as ledger:
        refunds(ledger, 6)
        ledger.compare_with_archive()
        store.delete(lambda e: e.sequence in (3, 6))
        archive.tamper(4, lambda e: e.with_changes(event_type="EDITED_IN_THE_ARCHIVE"))

        differences = {d.sequence: d for d in ledger.compare_with_archive().differences}
        assert differences[3].kind is DifferenceKind.MISSING_FROM_PRIMARY
        assert differences[6].kind is DifferenceKind.MISSING_FROM_PRIMARY
        assert differences[4].original is OriginalCopy.PRIMARY


def test_an_entry_modified_before_it_was_copied_is_not_copied():
    store = InMemoryLedgerStore()
    with open_ledger(store) as ledger:
        for i in range(4):
            ledger.add_entry("EVENT", {"n": i})
    store.tamper(3, lambda e: e.with_changes(event_type="FORGED"))

    archive = InMemoryLedgerStore()
    with open_ledger(store, archive=archive) as ledger:
        ledger.compare_with_archive()
        status = ledger.archive_status()
        assert len(archive) == 2
        assert not status.healthy
        assert status.archived_through == 2
        assert "Entry 3" in status.problem


def test_archive_and_primary_cannot_be_the_same_store():
    store = InMemoryLedgerStore()
    with pytest.raises(ValueError):
        open_ledger(store, archive=store)


# Recorded erasures

def test_an_erasure_appends_a_record_and_erases_both_copies():
    store, archive = InMemoryLedgerStore(), InMemoryLedgerStore()
    with open_ledger(store, archive=archive) as ledger:
        entries = refunds(ledger, 2)
        ledger.compare_with_archive()
        assert ledger.redact_payload(entries[0].id)

        head = store.get_head()
        assert head.event_type == REDACTION_EVENT
        assert entries[0].id in head.payload_json and "support-1" not in head.payload_json
        assert store.get_by_id(entries[0].id).payload_json is None
        ledger.compare_with_archive()
        assert archive.get_by_id(entries[0].id).payload_json is None
        assert ledger.verify().valid
    assert archive_verifier(archive).verify().valid


def test_a_payload_set_to_null_in_the_database_is_detected_and_not_laundered():
    store = InMemoryLedgerStore()
    with open_ledger(store) as ledger:
        entries = refunds(ledger, 3)
        store.redact_payload(entries[2].id)  # UPDATE ... SET payload = NULL
        result = ledger.verify()
        assert result.failure is VerificationFailure.UNRECORDED_REDACTION
        assert result.failed_at_sequence == 3

        assert ledger.redact_payload(entries[2].id)  # already missing: no record is written
        assert len(store) == 3
        assert ledger.verify().failure is VerificationFailure.UNRECORDED_REDACTION


def test_an_archive_that_refuses_the_erasure_is_reported():
    class RefusingArchive(InMemoryLedgerStore):
        def redact_payload(self, entry_id):
            raise PermissionError("UPDATE permission denied on table provance_archive")

    store = InMemoryLedgerStore()
    with open_ledger(store, archive=RefusingArchive()) as ledger:
        [entry] = refunds(ledger, 1)
        ledger.compare_with_archive()
        with pytest.raises(LedgerArchiveError, match="not in the archive"):
            ledger.redact_payload(entry.id)
        assert store.get_by_id(entry.id).payload_json is None


def test_library_event_types_are_reserved():
    with open_ledger() as ledger:
        with pytest.raises(ValueError, match="reserved"):
            ledger.add_entry("provance.retention", {})
        with pytest.raises(ValueError, match="reserved"):
            ledger.add_entry("PROVANCE.anything", {})


# Retention

def retention_setup(clock, **settings):
    """Five entries on day 0, three on day 10."""
    store, archive = InMemoryLedgerStore(), InMemoryLedgerStore()
    ledger = open_ledger(store, clock=clock, archive=archive if settings.pop("with_archive", True) else None, **settings)
    for i in range(1, 9):
        if i == 6:
            clock.now += timedelta(days=10)
        ledger.add_entry("EVENT", {"n": i})
    return ledger, store, archive


def test_old_entries_leave_the_primary_store_and_the_ledger_still_verifies():
    clock = Clock()
    ledger, store, archive = retention_setup(clock)
    with ledger:
        result = ledger.apply_retention(timedelta(days=7))
        assert result.applied, result.message
        assert (result.removed_entries, result.first_sequence) == (4, 5)
        assert result.record.event_type == RETENTION_EVENT and result.record.sequence == 9

        verification = ledger.verify(checkpoint=result.checkpoint)
        assert verification.valid, verification.message
        assert verification.first_sequence == 5
        assert "removed by retention" in verification.message

        comparison = ledger.compare_with_archive()
        assert comparison.identical and comparison.removed_by_retention == 4
    full = archive_verifier(archive).verify()
    assert full.valid and full.first_sequence == 1 and full.last_sequence == 9


def test_retention_runs_again_and_moves_the_start_forward():
    clock = Clock()
    ledger, store, _ = retention_setup(clock)
    with ledger:
        assert ledger.apply_retention(timedelta(days=7)).applied
        clock.now += timedelta(days=10)
        ledger.add_entry("EVENT", {"n": 10})
        second = ledger.apply_retention(timedelta(days=7))
        assert second.applied and second.first_sequence == 9
        assert ledger.verify().valid


@pytest.mark.parametrize("case", ["nothing_old", "no_archive", "tampered_primary", "different_archive"])
def test_nothing_is_removed_when_it_is_not_safe(case):
    clock = Clock()
    ledger, store, archive = retention_setup(clock, with_archive=case != "no_archive")
    with ledger:
        keep_for = timedelta(days=30 if case == "nothing_old" else 7)
        if case == "tampered_primary":
            store.tamper(2, lambda e: e.with_changes(event_type="FORGED"))
        if case == "different_archive":
            ledger.compare_with_archive()
            archive.tamper(3, lambda e: e.with_changes(event_type="EDITED_IN_THE_ARCHIVE"))
        result = ledger.apply_retention(keep_for)
        assert not result.applied
        assert len(store) == 8


def test_retention_without_an_archive_when_allowed():
    clock = Clock()
    ledger, store, _ = retention_setup(clock, with_archive=False, retention_require_archive=False)
    with ledger:
        assert ledger.apply_retention(timedelta(days=7)).applied
        assert ledger.verify().valid


def test_deleting_the_first_entries_without_a_record_is_detected():
    clock = Clock()
    ledger, store, _ = retention_setup(clock)
    with ledger:
        store.delete(lambda e: e.sequence < 3)
        result = ledger.verify()
        assert result.failure is VerificationFailure.SEQUENCE_GAP and result.failed_at_sequence == 1


def test_a_forged_retention_record_needs_the_key():
    clock = Clock()
    ledger, store, _ = retention_setup(clock)
    with ledger:
        store.delete(lambda e: e.sequence < 5)
        anchor = next(iter(store.read(1)))
        payload = f'{{"firstHash":"{anchor.hash}","firstSequence":{anchor.sequence}}}'

        def forge(e):
            forged = e.with_changes(event_type=RETENTION_EVENT, payload_json=payload, payload_hash=sha256_hex(payload))
            return forged.with_changes(hash=compute_entry_hash(forged))

        store.tamper(8, forge)
        result = ledger.verify()
        assert result.failure is VerificationFailure.INVALID_SIGNATURE and result.failed_at_sequence == 8


def test_retention_runs_on_its_own_when_configured():
    clock = Clock()
    ledger, store, _ = retention_setup(clock, retention_keep_for=timedelta(days=7),
                                       retention_interval=timedelta(milliseconds=50))
    with ledger:
        deadline = time.monotonic() + 10
        while len(store) == 8 and time.monotonic() < deadline:
            time.sleep(0.02)
        assert next(iter(store.read(1))).sequence == 5
        assert ledger.verify().valid
