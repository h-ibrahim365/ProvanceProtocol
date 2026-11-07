"""SqlLedgerStore contract: the same behavior on every database.

SQLite runs everywhere. PostgreSQL runs when PROVANCE_TEST_POSTGRES_URL points to a database (as in CI).
"""

from __future__ import annotations

import os
import sqlite3
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta

import pytest

from provance import (
    InMemoryLedgerStore,
    LedgerConcurrencyError,
    ProvanceLedger,
    SqlLedgerStore,
    VerificationFailure,
    connection_factory,
)

from conftest import LEDGER_ID, SECRET, seal
from test_sql import COLUMNS, sqlite_time

POSTGRES_URL = os.environ.get("PROVANCE_TEST_POSTGRES_URL")


@pytest.fixture(params=["sqlite", "postgresql"])
def backend(request, tmp_path):
    """Yields ``(make_store, execute)``: a store factory on one fresh table, and a raw SQL helper."""
    if request.param == "sqlite":
        url = f"sqlite:///{tmp_path / 'audit.db'}"
        table = "provance_ledger"
    else:
        if not POSTGRES_URL:
            pytest.skip("PROVANCE_TEST_POSTGRES_URL is not set")
        pytest.importorskip("psycopg")
        url = POSTGRES_URL
        table = "provance_py_" + uuid.uuid4().hex[:10]
    factory, dialect = connection_factory(url, read_only=False)

    def execute(sql, params=()):
        connection = factory()
        try:
            cursor = connection.cursor()
            cursor.execute(sql, params)
            rows = cursor.fetchall() if cursor.description else None
            connection.commit()
            return rows
        finally:
            connection.close()

    def make_store(**kwargs):
        return SqlLedgerStore(factory, dialect, table=table, **kwargs)

    make_store.table = table
    make_store.dialect = dialect
    make_store.factory = factory
    yield make_store, execute
    if request.param == "postgresql":
        execute(f"DROP TABLE IF EXISTS {table}")


def connection_factory_for(make_store):
    return make_store.factory, make_store.dialect


def open_ledger(store, **settings):
    settings.setdefault("retry_base_delay", timedelta(milliseconds=1))
    return ProvanceLedger(ledger_id=LEDGER_ID, secret_key=SECRET, store=store, **settings)


def test_round_trip_is_exact(backend):
    make_store, _ = backend
    store = make_store()
    assert store.get_head() is None  # creates the table
    entries = seal(5)
    store.append(entries)
    assert list(store.read()) == entries
    assert store.get_head() == entries[-1]
    assert store.get_by_id(entries[2].id.upper()) == entries[2]
    assert store.get_by_id(str(uuid.uuid4())) is None
    assert [e.sequence for e in store.read(4)] == [4, 5]


def test_paging(backend):
    make_store, _ = backend
    store = make_store(read_batch_size=3)
    store.append(seal(10))
    assert [e.sequence for e in store.read()] == list(range(1, 11))
    assert [e.sequence for e in store.read(7)] == [7, 8, 9, 10]


def test_conflicts_are_reported_and_batches_are_atomic(backend):
    make_store, execute = backend
    store = make_store()
    entries = seal(4)
    store.append(entries[:2])

    with pytest.raises(LedgerConcurrencyError):
        store.append([entries[1]])  # same sequence and id
    other = seal(3)  # different ids, sequences 1..3
    with pytest.raises(LedgerConcurrencyError):
        store.append([entries[2], entries[3], other[1]])  # seq 3, 4, then 2 again with another id
    assert execute(f"SELECT COUNT(*) FROM {make_store.table}")[0][0] == 2  # 3 and 4 rolled back
    store.append(entries[2:])
    assert [e.sequence for e in store.read()] == [1, 2, 3, 4]


def test_redaction(backend):
    make_store, execute = backend
    store = make_store()
    entries = seal(2)
    store.append(entries)
    assert store.redact_payload(entries[0].id)
    assert not store.redact_payload(str(uuid.uuid4()))
    redacted = store.get_by_id(entries[0].id)
    assert redacted.is_redacted and redacted.hash == entries[0].hash
    assert execute(f"SELECT COUNT(*) FROM {make_store.table} WHERE redacted_at IS NOT NULL")[0][0] == 1


def test_delete_before_and_an_archive_table_in_the_same_database(backend):
    make_store, execute = backend
    store = make_store()
    archive = SqlLedgerStore(*connection_factory_for(make_store), table=make_store.table + "_archive")
    try:
        with open_ledger(store, archive=archive, retention_require_archive=True) as ledger:
            for i in range(4):
                ledger.add_entry("EVENT", {"n": i})
            assert ledger.compare_with_archive().identical
        assert store.delete_before(3) == 2
        assert [e.sequence for e in store.read(1)] == [3, 4]
        assert [e.sequence for e in archive.read(1)] == [1, 2, 3, 4]
    finally:
        if make_store.dialect != "sqlite":
            execute(f"DROP TABLE IF EXISTS {make_store.table}_archive")


def test_ledger_on_sql_two_instances_and_tampering(backend):
    make_store, execute = backend
    with open_ledger(make_store(), max_batch_size=16) as a, open_ledger(make_store(), max_batch_size=16) as b:
        with ThreadPoolExecutor(8) as pool:
            futures = [pool.submit((a if i % 2 else b).add_entry, "EVENT", {"n": i}) for i in range(200)]
            assert {f.result(60).sequence for f in futures} == set(range(1, 201))
        result = a.verify()
        assert result.valid and result.entries_verified == 200, result.message

        execute(f"UPDATE {make_store.table} SET event_type = 'ROLE_REVOKED' WHERE seq = 50")
        tampered = a.verify()
        assert tampered.failure is VerificationFailure.HASH_MISMATCH and tampered.failed_at_sequence == 50


def test_existing_table_is_reused_and_creation_can_be_disabled(backend):
    make_store, _ = backend
    statements = make_store().create_table_statements()
    assert "UNIQUE INDEX" in statements[1] and make_store.table in statements[0]

    with pytest.raises(Exception):
        make_store(create_table=False).get_head()  # no table yet, none created
    first = make_store()
    first.append(seal(1))
    second = make_store()  # finds the table, does not recreate it
    assert second.get_head().sequence == 1


def test_sqlite_values_match_the_net_and_java_stores(tmp_path):
    # Uppercase ids and "yyyy-MM-dd HH:mm:ss.fff+00:00" text, so the three libraries share one file.
    store = SqlLedgerStore.from_url(f"sqlite:///{tmp_path / 'audit.db'}")
    entries = seal(1)
    store.append(entries)
    db = sqlite3.connect(tmp_path / "audit.db")
    row = db.execute(f"SELECT {COLUMNS} FROM provance_ledger").fetchone()
    assert row[0] == entries[0].id.upper()
    assert row[4] == sqlite_time(entries[0])
    db.close()


def test_store_rejects_unsafe_identifiers_and_read_only_dialects():
    with pytest.raises(ValueError):
        SqlLedgerStore(lambda: None, "sqlite", table="x; DROP TABLE y")
    with pytest.raises(ValueError):
        SqlLedgerStore(lambda: None, "oracle")


def test_in_memory_store_follows_the_same_contract():
    store = InMemoryLedgerStore()
    entries = seal(3)
    store.append(entries[:2])
    with pytest.raises(LedgerConcurrencyError):
        store.append([entries[1]])
    store.append(entries[2:])
    assert list(store.read(2)) == entries[1:]
