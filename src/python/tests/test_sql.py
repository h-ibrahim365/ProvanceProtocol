"""Reading the ledger table written by the .NET (Entity Framework Core) and Java (JDBC) stores.

SQLite runs everywhere. PostgreSQL runs when PROVANCE_TEST_POSTGRES_URL points to a database (as in CI).
"""

from __future__ import annotations

import os
import sqlite3
import uuid

import pytest

from provance import HmacVerifier, LedgerVerifier, SqlLedgerReader, VerificationFailure, connect, format_timestamp

from conftest import LEDGER_ID, SECRET, seal

COLUMNS = ("id, format_version, ledger_id, seq, sealed_at, event_type, payload, payload_hash, previous_hash, "
           "key_id, entry_hash, signature")

SQLITE_DDL = """CREATE TABLE provance_ledger (id text NOT NULL, format_version integer NOT NULL, ledger_id text NOT NULL,
seq integer NOT NULL, sealed_at text NOT NULL, event_type text NOT NULL, payload text, payload_hash text NOT NULL,
previous_hash text NOT NULL, key_id text NOT NULL, entry_hash text NOT NULL, signature text NOT NULL,
redacted_at text, PRIMARY KEY (id))"""

POSTGRES_DDL = """CREATE TABLE {t} (id uuid NOT NULL, format_version integer NOT NULL, ledger_id varchar(128) NOT NULL,
seq bigint NOT NULL, sealed_at timestamp with time zone NOT NULL, event_type varchar(200) NOT NULL, payload text,
payload_hash varchar(64) NOT NULL, previous_hash varchar(64) NOT NULL, key_id varchar(128) NOT NULL,
entry_hash varchar(64) NOT NULL, signature varchar(1024) NOT NULL, redacted_at timestamp with time zone,
PRIMARY KEY (id))"""


def sqlite_time(entry) -> str:
    # Entity Framework Core and the Java store write "2026-11-06 12:00:01.123+00:00" on SQLite.
    return format_timestamp(entry.timestamp).replace("T", " ").replace("Z", "+00:00")


def sqlite_db(path, entries):
    db = sqlite3.connect(path)
    db.execute(SQLITE_DDL)
    db.execute("CREATE UNIQUE INDEX provance_ledger_seq_unique ON provance_ledger (seq)")
    db.executemany(
        f"INSERT INTO provance_ledger ({COLUMNS}) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [(e.id.upper(), e.format_version, e.ledger_id, e.sequence, sqlite_time(e), e.event_type, e.payload_json,
          e.payload_hash, e.previous_hash, e.key_id, e.hash, e.signature) for e in entries])
    db.commit()
    return db


def test_sqlite_table_written_by_the_stores_verifies(tmp_path):
    entries = seal(7)
    sqlite_db(tmp_path / "audit.db", entries).close()

    connection, dialect = connect(f"sqlite:///{tmp_path / 'audit.db'}")
    reader = SqlLedgerReader(connection, dialect, batch_size=3)  # several pages
    read = list(reader.read())
    result = LedgerVerifier(reader, HmacVerifier({"k1": SECRET}), LEDGER_ID).verify()

    assert [e.sequence for e in read] == list(range(1, 8))
    assert read == entries  # ids lower-cased, timestamps parsed back exactly
    assert result.valid, result.message
    connection.close()


def test_sqlite_connection_is_read_only(tmp_path):
    sqlite_db(tmp_path / "audit.db", seal(1)).close()
    connection, _ = connect(f"sqlite:///{tmp_path / 'audit.db'}")
    with pytest.raises(sqlite3.OperationalError):
        connection.execute("DELETE FROM provance_ledger")
    connection.close()


def test_tampering_in_sqlite_is_detected(tmp_path):
    db = sqlite_db(tmp_path / "audit.db", seal(4))
    db.execute("UPDATE provance_ledger SET event_type = 'ROLE_REVOKED' WHERE seq = 2")
    db.commit()

    result = LedgerVerifier(SqlLedgerReader(db, "sqlite"), HmacVerifier({"k1": SECRET}), LEDGER_ID).verify()
    assert result.failure is VerificationFailure.HASH_MISMATCH
    assert result.failed_at_sequence == 2
    db.close()


def test_reader_rejects_unsafe_identifiers():
    with pytest.raises(ValueError):
        SqlLedgerReader(None, "sqlite", table="ledger; DROP TABLE x")
    with pytest.raises(ValueError):
        SqlLedgerReader(None, "unknown-dialect")


@pytest.mark.skipif(not os.environ.get("PROVANCE_TEST_POSTGRES_URL"), reason="PROVANCE_TEST_POSTGRES_URL is not set")
def test_postgresql_table_written_by_the_stores_verifies():
    psycopg = pytest.importorskip("psycopg")
    url = os.environ["PROVANCE_TEST_POSTGRES_URL"]
    table = "provance_py_" + uuid.uuid4().hex[:10]
    entries = seal(25)

    with psycopg.connect(url, autocommit=True) as admin:
        admin.execute(POSTGRES_DDL.format(t=table))
        admin.execute(f"CREATE UNIQUE INDEX {table}_seq_unique ON {table} (seq)")
        with admin.cursor() as cur:
            cur.executemany(
                f"INSERT INTO {table} ({COLUMNS}) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                [(uuid.UUID(e.id), e.format_version, e.ledger_id, e.sequence, e.timestamp, e.event_type, e.payload_json,
                  e.payload_hash, e.previous_hash, e.key_id, e.hash, e.signature) for e in entries])
        try:
            connection, dialect = connect(url)
            reader = SqlLedgerReader(connection, dialect, table=table, batch_size=10)
            assert list(reader.read()) == entries
            result = LedgerVerifier(reader, HmacVerifier({"k1": SECRET}), LEDGER_ID).verify()
            assert result.valid, result.message
            assert result.entries_verified == 25

            admin.execute(f"UPDATE {table} SET payload = '{{}}' WHERE seq = 12")
            tampered = LedgerVerifier(reader, HmacVerifier({"k1": SECRET}), LEDGER_ID).verify()
            assert tampered.failure is VerificationFailure.PAYLOAD_HASH_MISMATCH
            assert tampered.failed_at_sequence == 12
            connection.close()
        finally:
            admin.execute(f"DROP TABLE {table}")
