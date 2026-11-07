"""The ledger and its writer: integrity, configuration, failures, concurrency, shutdown, checkpoints."""

from __future__ import annotations

import asyncio
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import timedelta
from decimal import Decimal

import pytest

from provance import (
    AuditedPayload,
    FileCheckpointSink,
    HmacLedgerSigner,
    InMemoryLedgerStore,
    LedgerCheckpoint,
    LedgerOverloadedError,
    LedgerStoreError,
    LedgerUnavailableError,
    LedgerVerifier,
    PayloadError,
    ProvanceLedger,
    QueueFullBehavior,
    VerificationFailure,
    canonical_payload,
    derive_genesis_hash,
    read_latest_checkpoint,
)

from conftest import LEDGER_ID, SECRET


def open_ledger(store=None, **settings) -> ProvanceLedger:
    settings.setdefault("retry_base_delay", timedelta(milliseconds=1))
    settings.setdefault("shutdown_timeout", timedelta(seconds=10))
    return ProvanceLedger(ledger_id=settings.pop("ledger_id", LEDGER_ID), secret_key=settings.pop("secret_key", SECRET),
                          store=store if store is not None else InMemoryLedgerStore(), **settings)


class FaultyStore(InMemoryLedgerStore):
    """Injects failures: before writing, after writing (lost acknowledgement), after the first entry."""

    def __init__(self, faults=None, gate: threading.Event | None = None, fail_get_by_id=()):
        super().__init__()
        self.faults = faults or {}
        self.gate = gate
        self.fail_get_by_id = set(fail_get_by_id)
        self.append_calls = 0
        self.get_by_id_calls = 0

    def append(self, entries):
        call = self.append_calls
        self.append_calls += 1
        if self.gate is not None:
            self.gate.wait()
        fault = self.faults.get(call)
        if fault == "before":
            raise LedgerStoreError("Simulated timeout (nothing written).")
        super().append(entries if fault != "first" else entries[:1])
        if fault == "after":
            raise LedgerStoreError("Simulated timeout (batch was written).")
        if fault == "first":
            raise LedgerStoreError("Simulated failure after the first entry.")

    def get_by_id(self, entry_id):
        call = self.get_by_id_calls
        self.get_by_id_calls += 1
        if call in self.fail_get_by_id:
            raise LedgerStoreError("Simulated read timeout.")
        return super().get_by_id(entry_id)


def assert_chain(ledger, count):
    result = ledger.verify()
    assert result.valid, result.message
    assert result.entries_verified == count


# --- integrity ---------------------------------------------------------------------------------------------------


def test_entries_are_chained_signed_and_verifiable():
    with open_ledger() as ledger:
        first = ledger.add_entry("EVENT", AuditedPayload(actor_id="u1", description="first"))
        for i in range(9):
            ledger.add_entry("EVENT", {"n": i})
        assert first.previous_hash == derive_genesis_hash(LEDGER_ID)
        assert first.payload() == {"actorId": "u1", "description": "first"}
        assert_chain(ledger, 10)


@dataclass
class HttpLike:
    actor_id: str
    path: str
    client_ip: str | None = None


def test_every_field_of_a_payload_type_is_signed():
    store = InMemoryLedgerStore()
    with open_ledger(store) as ledger:
        entry = ledger.add_entry("HTTP", HttpLike("u1", "/api/pay", "10.0.0.1"))
        store.tamper(entry.sequence, lambda e: e.with_changes(payload_json=e.payload_json.replace("/api/pay", "/api/refund")))
        assert ledger.verify().failure is VerificationFailure.PAYLOAD_HASH_MISMATCH


def test_tampering_and_truncation_are_detected():
    store = InMemoryLedgerStore()
    with open_ledger(store) as ledger:
        for i in range(5):
            ledger.add_entry("EVENT", {"n": i})
        checkpoint = ledger.create_checkpoint()
        store.tamper(3, lambda e: e.with_changes(event_type="X"))
        assert ledger.verify().failure is VerificationFailure.HASH_MISMATCH
        store.tamper(3, lambda e: e.with_changes(event_type="EVENT"))
        store.delete(lambda e: e.sequence >= 4)
        assert ledger.verify().valid  # documented limit without checkpoint
        assert ledger.verify(checkpoint).failure is VerificationFailure.TRUNCATED


def test_redaction_and_key_rotation():
    store = InMemoryLedgerStore()
    with open_ledger(store) as before:
        old = before.add_entry("EVENT", AuditedPayload(actor_id="alice@example.com"))
    new_secret = "second-python-test-secret-0123456789"
    with open_ledger(store, signing_key_id="k2", secret_key=new_secret, retired_keys={"k1": SECRET}) as after:
        assert after.add_entry("EVENT", {"n": 2}).key_id == "k2"
        assert after.redact_payload(old.id)
        result = after.verify()
        assert result.valid and result.redacted_payloads == 1
        without_old_key = LedgerVerifier(store, HmacLedgerSigner("k2", new_secret), LEDGER_ID).verify()
        assert without_old_key.failure is VerificationFailure.UNKNOWN_KEY


def test_a_truncated_checkpoint_file_is_replaced_and_skipped_when_reading(tmp_path):
    from provance import read_latest_checkpoint

    with open_ledger() as ledger:
        ledger.add_entry("EVENT", {"n": 1})
        first = ledger.create_checkpoint()
        ledger.add_entry("EVENT", {"n": 2})
        second = ledger.create_checkpoint()
    sink = FileCheckpointSink(tmp_path)
    sink.publish(first)
    folder = tmp_path / LEDGER_ID
    (folder / "0000000000000002.json").write_text(second.to_json()[:20], encoding="utf-8")  # the process died while writing

    assert read_latest_checkpoint(tmp_path, LEDGER_ID) == first
    sink.publish(second)
    assert read_latest_checkpoint(tmp_path, LEDGER_ID) == second
    assert not [f for f in folder.iterdir() if f.name.startswith(".")]


def test_checkpoint_round_trip_and_file_sink(tmp_path):
    with open_ledger(checkpoint_directory=tmp_path, checkpoint_interval=timedelta(milliseconds=20)) as ledger:
        assert ledger.create_checkpoint() is None  # empty ledger
        ledger.add_entry("EVENT", {"n": 1})
        deadline = time.monotonic() + 5
        while read_latest_checkpoint(tmp_path, LEDGER_ID) is None and time.monotonic() < deadline:
            time.sleep(0.01)
        published = read_latest_checkpoint(tmp_path, LEDGER_ID)
        assert published is not None and published.sequence == 1
        assert (tmp_path / LEDGER_ID / "0000000000000001.json").exists()
        assert LedgerCheckpoint.from_json(published.to_json()) == published
        assert ledger.verify(published).valid
        FileCheckpointSink(tmp_path).publish(published)  # same head again: no error, no overwrite


# --- configuration --------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("settings", [
    {"ledger_id": ""}, {"ledger_id": "has space"}, {"secret_key": None}, {"secret_key": "too-short"},
    {"secret_key": "DEFAULT_INSECURE_KEY_CHANGE_ME"}, {"genesis_hash": "ABC"}, {"max_batch_size": 0},
])
def test_invalid_configuration_is_refused(settings):
    with pytest.raises(ValueError):
        open_ledger(**settings)


def test_no_store_is_refused():
    with pytest.raises(ValueError, match="No PROVANCE store"):
        ProvanceLedger(ledger_id=LEDGER_ID, secret_key=SECRET, store=None)


def test_arguments_are_validated_before_queueing():
    store = InMemoryLedgerStore()
    with open_ledger(store) as ledger:
        with pytest.raises(ValueError):
            ledger.add_entry(" ", {"n": 1})
        with pytest.raises(ValueError):
            ledger.add_entry("X" * 201, {"n": 1})
        with pytest.raises(PayloadError):
            ledger.add_entry("EVENT", None)
        with pytest.raises(PayloadError):
            ledger.add_entry("EVENT", {"n": float("nan")})
        with pytest.raises(PayloadError):
            ledger.add_entry("EVENT", object())
        assert len(store) == 0


def test_payload_serialization_matches_the_other_implementations():
    # Same canonical JSON as AuditedPayload in .NET and Java.
    assert canonical_payload(AuditedPayload(actor_id="user-42", description="Refund issued", data={"orderId": "A-1001"})) \
        == '{"actorId":"user-42","data":{"orderId":"A-1001"},"description":"Refund issued"}'
    assert canonical_payload({"d": Decimal("10.50"), "big": 2**60, "z": None, "t": (1, 2.0)}) \
        == '{"big":1152921504606847000,"d":10.5,"t":[1,2],"z":null}'
    assert canonical_payload({"n": -0.0, "e": 1e21, "s": 1e-7}) == '{"e":1e+21,"n":0,"s":1e-7}'


# --- writer -----------------------------------------------------------------------------------------------------------


def test_lost_acknowledgement_does_not_wedge_the_writer():
    store = FaultyStore(faults={1: "after"})
    with open_ledger(store) as ledger:
        assert [ledger.add_entry("EVENT", {"n": i}).sequence for i in range(5)] == [1, 2, 3, 4, 5]
        assert len(store) == 5
        assert_chain(ledger, 5)


def test_lost_acknowledgement_followed_by_a_failed_lookup():
    store = FaultyStore(faults={0: "after"}, fail_get_by_id={0})
    with open_ledger(store) as ledger:
        assert ledger.add_entry("EVENT", {"n": 1}).sequence == 1
        assert ledger.add_entry("EVENT", {"n": 2}).sequence == 2
        assert len(store) == 2


def test_partially_persisted_batch_is_completed_without_duplicates():
    gate = threading.Event()
    store = FaultyStore(faults={1: "first"}, gate=gate)
    with open_ledger(store) as ledger:
        first = ledger.submit("EVENT", {"n": 0})
        while store.append_calls < 1:
            time.sleep(0.005)
        others = [ledger.submit("EVENT", {"n": i}) for i in range(1, 21)]
        time.sleep(0.05)
        gate.set()
        first.result(10)
        assert len({f.result(10).sequence for f in others}) == 20
        assert len(store) == 21
        assert_chain(ledger, 21)


def test_transient_failures_are_retried_then_reported():
    store = FaultyStore(faults={0: "before", 1: "before", 2: "before"})
    with open_ledger(store, max_append_attempts=3) as ledger:
        with pytest.raises(LedgerUnavailableError):
            ledger.add_entry("EVENT", {"n": 1})
        assert ledger.add_entry("EVENT", {"n": 2}).sequence == 1


def test_concurrent_producers_and_two_instances_share_one_ledger():
    shared = InMemoryLedgerStore()
    with open_ledger(shared, max_batch_size=8) as a, open_ledger(shared, max_batch_size=8) as b:
        with ThreadPoolExecutor(16) as pool:
            futures = [pool.submit((a if i % 2 else b).add_entry, "EVENT", {"n": i}) for i in range(1000)]
            sequences = {f.result(30).sequence for f in futures}
        assert sequences == set(range(1, 1001))
        assert_chain(a, 1000)


def test_close_drains_and_timeout_fails_instead_of_hanging():
    slow = FaultyStore()
    slow.gate = threading.Event()
    slow.gate.set()
    ledger = open_ledger(slow, max_batch_size=1)
    futures = [ledger.submit("EVENT", {"n": i}) for i in range(30)]
    ledger.close()
    assert all(f.result(5).sequence for f in futures)
    assert len(slow) == 30

    stuck = FaultyStore(gate=threading.Event())  # the database never answers
    ledger = open_ledger(stuck, shutdown_timeout=timedelta(milliseconds=200))
    in_flight = ledger.submit("EVENT", {"n": 0})
    while stuck.append_calls < 1:
        time.sleep(0.005)
    queued = [ledger.submit("EVENT", {"n": i}) for i in range(1, 5)]
    started = time.monotonic()
    ledger.close()
    assert time.monotonic() - started < 7
    for future in queued:
        assert isinstance(future.exception(timeout=1), LedgerUnavailableError)
    stuck.gate.set()  # the database answers late: the entry in flight is still stored
    assert in_flight.result(5).sequence == 1


def test_closed_ledger_refuses_entries():
    ledger = open_ledger()
    ledger.close()
    ledger.close()  # idempotent
    with pytest.raises(LedgerUnavailableError):
        ledger.add_entry("EVENT", {"n": 1})


def test_full_queue_fails_fast_when_configured():
    gate = threading.Event()
    store = FaultyStore(gate=gate)
    with open_ledger(store, queue_capacity=1, queue_full_behavior=QueueFullBehavior.FAIL_FAST) as ledger:
        in_writer = ledger.submit("EVENT", {"n": 1})
        while store.append_calls < 1:
            time.sleep(0.005)
        in_queue = ledger.submit("EVENT", {"n": 2})
        with pytest.raises(LedgerOverloadedError):
            ledger.submit("EVENT", {"n": 3})
        gate.set()
        assert in_writer.result(5).sequence == 1
        assert in_queue.result(5).sequence == 2


def test_writer_refuses_a_store_holding_another_ledger():
    store = InMemoryLedgerStore()
    with open_ledger(store, ledger_id="another-ledger") as other:
        other.add_entry("EVENT", {"n": 1})
    with open_ledger(store, max_append_attempts=2) as ledger:
        with pytest.raises(LedgerUnavailableError, match="another-ledger"):
            ledger.add_entry("EVENT", {"n": 2})


def test_async_api():
    async def run(ledger):
        entries = await asyncio.gather(*(ledger.add_entry_async("EVENT", {"n": i}) for i in range(50)))
        return {e.sequence for e in entries}

    with open_ledger() as ledger:
        assert asyncio.run(run(ledger)) == set(range(1, 51))
        assert_chain(ledger, 50)


def test_async_api_never_blocks_the_event_loop_on_a_full_queue():
    gate = threading.Event()
    store = FaultyStore(gate=gate)

    async def run(ledger):
        first = asyncio.ensure_future(ledger.add_entry_async("EVENT", {"n": 1}))
        while store.append_calls < 1:  # the writer holds entry 1 and waits on the store
            await asyncio.sleep(0.005)
        second = asyncio.ensure_future(ledger.add_entry_async("EVENT", {"n": 2}))  # fills the queue (capacity 1)
        while not ledger._writer._queue.full():
            await asyncio.sleep(0.001)
        ticks = 0

        async def ticker():
            nonlocal ticks
            while True:
                ticks += 1
                await asyncio.sleep(0.01)

        clock = asyncio.ensure_future(ticker())
        with pytest.raises(asyncio.TimeoutError):  # the timeout covers the wait for room in the queue
            await asyncio.wait_for(ledger.add_entry_async("EVENT", {"n": 3}), 0.3)
        assert ticks >= 10  # other coroutines kept running meanwhile
        clock.cancel()
        gate.set()
        return (await first).sequence, (await second).sequence

    with open_ledger(store, queue_capacity=1) as ledger:
        assert asyncio.run(run(ledger)) == (1, 2)


def test_entries_submitted_while_closing_are_written_or_failed_never_left_waiting():
    for _ in range(20):
        ledger = open_ledger(InMemoryLedgerStore())
        futures, errors = [], []
        stop = threading.Event()

        def produce():
            while not stop.is_set():
                try:
                    futures.append(ledger.submit("EVENT", {"n": 1}))
                except LedgerUnavailableError:
                    errors.append(1)
                    return

        threads = [threading.Thread(target=produce) for _ in range(4)]
        for t in threads:
            t.start()
        time.sleep(0.01)
        ledger.close()
        stop.set()
        for t in threads:
            t.join(5)
        for future in futures:
            try:
                future.result(timeout=5)  # a TimeoutError here is the bug: an entry accepted after the writer stopped
            except LedgerUnavailableError:
                pass


@pytest.mark.skipif(not hasattr(os, "fork"), reason="needs fork")
@pytest.mark.filterwarnings("ignore:This process .* is multi-threaded:DeprecationWarning")
def test_ledger_inherited_through_fork_fails_instead_of_hanging():
    with open_ledger() as ledger:
        read_end, write_end = os.pipe()
        pid = os.fork()
        if pid == 0:  # child: the writer thread does not exist here
            try:
                ledger.add_entry("EVENT", {"n": 1}, timeout=2)
                code = b"hung-or-wrote"
            except LedgerUnavailableError:
                code = b"ok"
            except Exception:
                code = b"other"
            os.write(write_end, code)
            os._exit(0)
        os.close(write_end)
        os.waitpid(pid, 0)
        assert os.read(read_end, 64) == b"ok"
        os.close(read_end)
