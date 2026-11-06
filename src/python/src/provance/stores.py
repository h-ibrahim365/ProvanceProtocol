"""Where ledgers live: the store interface, an in-memory store, a SQL store, and readers for exports.

The SQL store uses the same table, columns and value formats as the .NET Entity Framework Core store and the
Java JDBC store, so a ledger written by one implementation is read by the others.
"""

from __future__ import annotations

import json
import re
import sqlite3
import threading
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import IO, Any, Callable, Iterable, Iterator, Protocol, Sequence
from urllib.parse import unquote, urlparse

from .errors import LedgerConcurrencyError, LedgerStoreError
from .format import format_timestamp, normalize_timestamp, parse_timestamp
from .models import LedgerEntry, normalize_id

DEFAULT_TABLE = "provance_ledger"

_IDENTIFIER = re.compile(r"\A[A-Za-z_][A-Za-z0-9_]{0,62}\Z")
_COLUMNS = ("id, format_version, ledger_id, seq, sealed_at, event_type, payload, payload_hash, "
            "previous_hash, key_id, entry_hash, signature")
_ID_CHUNK = 500


# ---------------------------------------------------------------------------------------------------------------------
# Store interface


class LedgerStore(Protocol):
    """Persistence of one ledger. Implementations store what the writer sealed and enforce uniqueness of sequence
    and id, which keeps the chain linear when several application instances write. Must be thread safe.

    ``append`` raises :class:`LedgerConcurrencyError` when a sequence or id already exists, and
    :class:`LedgerStoreError` (or any other exception) for transient failures, which the writer retries.
    """

    def get_head(self) -> LedgerEntry | None: ...

    def append(self, entries: Sequence[LedgerEntry]) -> None: ...

    def get_by_id(self, entry_id: str) -> LedgerEntry | None: ...

    def read(self, from_sequence: int = 1) -> Iterable[LedgerEntry]: ...

    def redact_payload(self, entry_id: str) -> bool: ...


class InMemoryLedgerStore:
    """Thread safe in-memory store for tests and demos. Everything is lost when the process stops."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._entries: list[LedgerEntry] = []

    def __len__(self) -> int:
        with self._lock:
            return len(self._entries)

    def get_head(self) -> LedgerEntry | None:
        with self._lock:
            return self._entries[-1] if self._entries else None

    def append(self, entries: Sequence[LedgerEntry]) -> None:
        with self._lock:
            sequences = {e.sequence for e in self._entries}
            ids = {e.id for e in self._entries}
            batch_ids: set[str] = set()
            for i, entry in enumerate(entries):
                if entry.sequence in sequences:
                    raise LedgerConcurrencyError(f"An entry with sequence {entry.sequence} already exists.")
                if entry.id in ids or entry.id in batch_ids:
                    raise LedgerConcurrencyError(f"An entry with id {entry.id} already exists.")
                if i and entry.sequence != entries[i - 1].sequence + 1:
                    raise ValueError("Entries of a batch must have contiguous ascending sequences.")
                batch_ids.add(entry.id)
            self._entries.extend(entries)
            self._entries.sort(key=lambda e: e.sequence)

    def get_by_id(self, entry_id: str) -> LedgerEntry | None:
        with self._lock:
            return next((e for e in self._entries if e.id == entry_id), None)

    def read(self, from_sequence: int = 1) -> Iterator[LedgerEntry]:
        with self._lock:
            snapshot = [e for e in self._entries if e.sequence >= from_sequence]
        return iter(snapshot)

    def redact_payload(self, entry_id: str) -> bool:
        with self._lock:
            for i, e in enumerate(self._entries):
                if e.id == entry_id:
                    self._entries[i] = e.with_changes(payload_json=None)
                    return True
            return False

    # Test helpers: edit rows the way an attacker with database access would.

    def tamper(self, sequence: int, change: Callable[[LedgerEntry], LedgerEntry]) -> None:
        with self._lock:
            for i, e in enumerate(self._entries):
                if e.sequence == sequence:
                    self._entries[i] = change(e)

    def delete(self, predicate: Callable[[LedgerEntry], bool]) -> None:
        with self._lock:
            self._entries = [e for e in self._entries if not predicate(e)]


class ListSource:
    """Entries already in memory, read only."""

    def __init__(self, entries: Iterable[LedgerEntry]):
        self.entries = sorted(entries, key=lambda e: e.sequence)

    def read(self, from_sequence: int = 1) -> Iterator[LedgerEntry]:
        return (e for e in self.entries if e.sequence >= from_sequence)


# ---------------------------------------------------------------------------------------------------------------------
# JSON Lines exports


class JsonLinesReader:
    """Reads a JSON Lines export: one entry per line, field names as in :meth:`LedgerEntry.to_export`."""

    def __init__(self, path: str | Path):
        self.path = Path(path)

    def read(self, from_sequence: int = 1) -> Iterator[LedgerEntry]:
        with self.path.open("r", encoding="utf-8", newline="\n") as f:
            for number, line in enumerate(f, start=1):
                if not line.strip():
                    continue
                try:
                    entry = LedgerEntry.from_export(json.loads(line))
                except ValueError as e:
                    raise ValueError(f"{self.path}, line {number}: {e}") from None
                if entry.sequence >= from_sequence:
                    yield entry


def write_json_lines(entries: Iterable[LedgerEntry], out: IO[str]) -> int:
    """Writes entries as JSON Lines. Returns the number of entries written."""
    count = 0
    for entry in entries:
        line = json.dumps(entry.to_export(), ensure_ascii=False, separators=(",", ":"))
        # U+2028 and U+2029 are valid in JSON but split lines in some tools: escape them.
        out.write(line.replace("\u2028", "\\u2028").replace("\u2029", "\\u2029"))
        out.write("\n")
        count += 1
    return count


# ---------------------------------------------------------------------------------------------------------------------
# SQL


@dataclass(frozen=True)
class Dialect:
    """Database specific details: parameter style, row limit syntax, column types and value formats."""

    name: str
    placeholder: str  # "%s", "?" or ":" (numbered)
    limit_template: str  # appended after "ORDER BY seq"; {n} is the page size
    types: tuple[str, ...] | None  # id, int, ledger_id, seq, time, event_type, payload, hash, signature, table suffix

    def param(self, position: int) -> str:
        """Placeholder of the parameter at ``position`` (1-based)."""
        return f":{position}" if self.placeholder == ":" else self.placeholder

    def params(self, count: int) -> str:
        if self.placeholder == ":":
            return ", ".join(f":{i}" for i in range(1, count + 1))
        return ", ".join([self.placeholder] * count)

    def page_query(self, table: str, size: int) -> str:
        return (f"SELECT {_COLUMNS} FROM {table} WHERE seq >= {self.params(1)} ORDER BY seq "
                + self.limit_template.format(n=int(size)))

    def create_table_statements(self, table: str, index_name: str) -> list[str]:
        if self.types is None:
            raise ValueError(f"Writing to {self.name} is not supported by the Python store.")
        id_, int_, ledger, seq, time_, event, payload, hash_, sig, suffix = self.types
        return [
            f"CREATE TABLE {table} (id {id_} NOT NULL, format_version {int_} NOT NULL, ledger_id {ledger} NOT NULL, "
            f"seq {seq} NOT NULL, sealed_at {time_} NOT NULL, event_type {event} NOT NULL, payload {payload}, "
            f"payload_hash {hash_} NOT NULL, previous_hash {hash_} NOT NULL, key_id {ledger} NOT NULL, "
            f"entry_hash {hash_} NOT NULL, signature {sig} NOT NULL, redacted_at {time_}, PRIMARY KEY (id)){suffix}",
            f"CREATE UNIQUE INDEX {index_name} ON {table} (seq)",
        ]

    def bind_id(self, entry_id: str) -> Any:
        if self.name == "postgresql":
            return uuid.UUID(entry_id)
        if self.name == "sqlite":
            return entry_id.upper()  # as Entity Framework Core and the Java store write it
        return entry_id

    def bind_time(self, value: datetime) -> Any:
        if self.name == "postgresql":
            return normalize_timestamp(value)
        if self.name == "mysql":
            return normalize_timestamp(value).replace(tzinfo=None)  # datetime(6) holds UTC
        text = format_timestamp(value)  # 2026-01-15T09:30:00.123Z
        if self.name == "sqlite":
            return text.replace("T", " ").replace("Z", "+00:00")
        return text.replace("T", " ").replace("Z", " +00:00")  # SQL Server datetimeoffset


DIALECTS = {
    "postgresql": Dialect("postgresql", "%s", "LIMIT {n}", (
        "uuid", "integer", "varchar(128)", "bigint", "timestamp with time zone", "varchar(200)", "text",
        "varchar(64)", "varchar(1024)", "")),
    "sqlserver": Dialect("sqlserver", "%s", "OFFSET 0 ROWS FETCH NEXT {n} ROWS ONLY", (
        "uniqueidentifier", "int", "nvarchar(128)", "bigint", "datetimeoffset", "nvarchar(200)", "nvarchar(max)",
        "nvarchar(64)", "nvarchar(1024)", "")),
    "mysql": Dialect("mysql", "%s", "LIMIT {n}", (
        "char(36)", "int", "varchar(128)", "bigint", "datetime(6)", "varchar(200)", "longtext",
        "varchar(64)", "varchar(1024)", " CHARACTER SET utf8mb4 COLLATE utf8mb4_bin")),
    "sqlite": Dialect("sqlite", "?", "LIMIT {n}", (
        "text", "integer", "text", "integer", "text", "text", "text", "text", "text", "")),
    "oracle": Dialect("oracle", ":", "FETCH FIRST {n} ROWS ONLY", None),  # read only
}


def _check_identifier(name: str, value: str | None) -> None:
    if value is not None and not _IDENTIFIER.match(value):
        raise ValueError(f"{name} may only contain letters, digits and underscores (max 63 characters).")


class SqlLedgerReader:
    """Reads the ledger table created by the .NET, Java or Python stores, using one DB-API 2.0 connection.

    Rows are read page by page in sequence order, so memory use stays flat on large ledgers.
    """

    def __init__(self, connection: Any, dialect: str, table: str = DEFAULT_TABLE, schema: str | None = None,
                 batch_size: int = 1000):
        if dialect not in DIALECTS:
            raise ValueError(f"Unknown dialect '{dialect}'. Known: {', '.join(sorted(DIALECTS))}.")
        _check_identifier("table", table)
        _check_identifier("schema", schema)
        if batch_size < 1:
            raise ValueError("batch_size must be at least 1.")
        self.connection = connection
        self.dialect = DIALECTS[dialect]
        self.table = f"{schema}.{table}" if schema else table
        self.batch_size = batch_size

    def read(self, from_sequence: int = 1) -> Iterator[LedgerEntry]:
        return _read_pages(lambda: self.connection, False, self.dialect, self.table, self.batch_size, from_sequence)


class SqlLedgerStore:
    """Store on any DB-API 2.0 database: SQLite, PostgreSQL (psycopg), MySQL and MariaDB (PyMySQL),
    SQL Server (pymssql).

    ``connect`` returns a new connection for each operation. Pass a pooled factory in production, for example
    SQLAlchemy's ``engine.raw_connection``. :meth:`from_url` builds a simple factory from a URL.

    The table is created on first use when it does not exist (``create_table=False`` when your migrations create
    it; see :meth:`create_table_statements`). Conflicts are detected without parsing vendor error codes: when an
    insert fails, the store checks whether one of the batch's sequences or ids now exists. A batch is inserted in
    one transaction, completely or not at all.
    """

    def __init__(self, connect: Callable[[], Any], dialect: str, table: str = DEFAULT_TABLE,
                 schema: str | None = None, create_table: bool = True, read_batch_size: int = 1000):
        if dialect not in DIALECTS or DIALECTS[dialect].types is None:
            raise ValueError(f"Unsupported dialect '{dialect}'. Use sqlite, postgresql, mysql or sqlserver.")
        _check_identifier("table", table)
        _check_identifier("schema", schema)
        if read_batch_size < 1:
            raise ValueError("read_batch_size must be at least 1.")
        self._connect = connect
        self.dialect = DIALECTS[dialect]
        self.table = f"{schema}.{table}" if schema else table
        self._index = f"{table}_seq_unique"
        self._create_table = create_table
        self._read_batch_size = read_batch_size
        self._initialized = False
        self._init_lock = threading.Lock()

    @classmethod
    def from_url(cls, url: str, **kwargs: Any) -> "SqlLedgerStore":
        """Store from a URL: ``sqlite:///audit.db``, ``postgresql://...``, ``mysql://...``, ``mssql://...``."""
        factory, dialect = connection_factory(url, read_only=False)
        return cls(factory, dialect, **kwargs)

    def create_table_statements(self) -> list[str]:
        """Statements creating the table and its unique index on ``seq``, for your migrations."""
        return self.dialect.create_table_statements(self.table, self._index)

    def get_head(self) -> LedgerEntry | None:
        self._ensure_initialized()
        query = f"SELECT {_COLUMNS} FROM {self.table} WHERE seq = (SELECT MAX(seq) FROM {self.table})"
        rows = self._query("reading the head", query, ())
        return row_to_entry(rows[0]) if rows else None

    def get_by_id(self, entry_id: str) -> LedgerEntry | None:
        self._ensure_initialized()
        query = f"SELECT {_COLUMNS} FROM {self.table} WHERE id = {self.dialect.params(1)}"
        rows = self._query("reading an entry", query, (self.dialect.bind_id(normalize_id(entry_id)),))
        return row_to_entry(rows[0]) if rows else None

    def read(self, from_sequence: int = 1) -> Iterator[LedgerEntry]:
        self._ensure_initialized()
        return _read_pages(self._connect, True, self.dialect, self.table, self._read_batch_size, from_sequence)

    def append(self, entries: Sequence[LedgerEntry]) -> None:
        if not entries:
            return
        self._ensure_initialized()
        d = self.dialect
        insert = f"INSERT INTO {self.table} ({_COLUMNS}) VALUES ({d.params(12)})"
        rows = [(d.bind_id(e.id), e.format_version, e.ledger_id, e.sequence, d.bind_time(e.timestamp), e.event_type,
                 e.payload_json, e.payload_hash, e.previous_hash, e.key_id, e.hash, e.signature) for e in entries]
        try:
            connection = self._connect()
            try:
                cursor = connection.cursor()
                try:
                    cursor.executemany(insert, rows)
                finally:
                    cursor.close()
                connection.commit()
            except BaseException:
                _rollback_quietly(connection)
                raise
            finally:
                connection.close()
        except Exception as e:
            if self._conflict_exists(entries):
                raise LedgerConcurrencyError("An entry with the same sequence or id already exists.") from e
            raise LedgerStoreError(f"PROVANCE SQL: appending {len(entries)} entries to {self.table} failed: {e}") from e

    def redact_payload(self, entry_id: str) -> bool:
        self._ensure_initialized()
        d = self.dialect
        query = f"UPDATE {self.table} SET payload = NULL, redacted_at = {d.params(1)} WHERE id = {d.param(2)}"
        connection = self._connect()
        try:
            cursor = connection.cursor()
            try:
                cursor.execute(query, (d.bind_time(datetime.now(timezone.utc)), d.bind_id(normalize_id(entry_id))))
                updated = cursor.rowcount
            finally:
                cursor.close()
            connection.commit()
            return updated > 0
        except Exception as e:
            _rollback_quietly(connection)
            raise LedgerStoreError(f"PROVANCE SQL: redacting a payload failed: {e}") from e
        finally:
            connection.close()

    # internals

    def _query(self, what: str, query: str, params: tuple) -> list:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            try:
                cursor.execute(query, params)
                return cursor.fetchall()
            finally:
                cursor.close()
        except Exception as e:
            raise LedgerStoreError(f"PROVANCE SQL: {what} in {self.table} failed: {e}") from e
        finally:
            _rollback_quietly(connection)  # end the read transaction before returning the connection
            connection.close()

    def _conflict_exists(self, entries: Sequence[LedgerEntry]) -> bool:
        d = self.dialect
        try:
            rows = self._query("checking conflicts",
                               f"SELECT COUNT(*) FROM {self.table} WHERE seq >= {d.params(1)} AND seq <= {d.param(2)}",
                               (min(e.sequence for e in entries), max(e.sequence for e in entries)))
            if rows and rows[0][0]:
                return True
            for start in range(0, len(entries), _ID_CHUNK):
                chunk = entries[start:start + _ID_CHUNK]
                rows = self._query("checking conflicts",
                                   f"SELECT COUNT(*) FROM {self.table} WHERE id IN ({d.params(len(chunk))})",
                                   tuple(d.bind_id(e.id) for e in chunk))
                if rows and rows[0][0]:
                    return True
            return False
        except Exception:
            return False  # cannot tell: report the original failure, the writer will retry

    def _ensure_initialized(self) -> None:
        if self._initialized:
            return
        with self._init_lock:
            if self._initialized:
                return
            if self._create_table and not self._table_exists():
                try:
                    connection = self._connect()
                    try:
                        cursor = connection.cursor()
                        try:
                            for statement in self.create_table_statements():
                                cursor.execute(statement)
                        finally:
                            cursor.close()
                        connection.commit()
                    except BaseException:
                        _rollback_quietly(connection)
                        raise
                    finally:
                        connection.close()
                except Exception as e:
                    if not self._table_exists():  # accept only a concurrent creation by another instance
                        raise LedgerStoreError(f"PROVANCE SQL: creating the table {self.table} failed: {e}") from e
            self._initialized = True

    def _table_exists(self) -> bool:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            try:
                cursor.execute(f"SELECT 1 FROM {self.table} WHERE 1 = 0")
                cursor.fetchall()
                return True
            finally:
                cursor.close()
        except Exception:
            return False
        finally:
            _rollback_quietly(connection)
            connection.close()


def _read_pages(connect: Callable[[], Any], owns: bool, dialect: Dialect, table: str, size: int,
                from_sequence: int) -> Iterator[LedgerEntry]:
    query = dialect.page_query(table, size)
    next_sequence = from_sequence
    while True:
        connection = connect()
        try:
            cursor = connection.cursor()
            try:
                cursor.execute(query, (next_sequence,))
                rows = cursor.fetchall()
            finally:
                cursor.close()
        except Exception as e:
            raise LedgerStoreError(f"PROVANCE SQL: reading entries from {table} failed: {e}") from e
        finally:
            if owns:
                _rollback_quietly(connection)
                connection.close()
        for row in rows:
            yield row_to_entry(row)
        if len(rows) < size:
            return
        next_sequence = int(rows[-1][3]) + 1


def _rollback_quietly(connection: Any) -> None:
    try:
        connection.rollback()
    except Exception:
        pass


def row_to_entry(row: Any) -> LedgerEntry:
    """Converts a row of the ledger table (columns in the order of the SELECT) into an entry."""
    (id_, format_version, ledger_id, seq, sealed_at, event_type, payload,
     payload_hash, previous_hash, key_id, entry_hash, signature) = row
    return LedgerEntry(
        format_version=int(format_version),
        ledger_id=_text(ledger_id),
        sequence=int(seq),
        id=normalize_id(id_),
        timestamp=_time(sealed_at),
        event_type=_text(event_type),
        payload_json=None if payload is None else _text(payload),
        payload_hash=_text(payload_hash),
        previous_hash=_text(previous_hash),
        key_id=_text(key_id),
        hash=_text(entry_hash),
        signature=_text(signature),
    )


def _text(value: Any) -> str:
    if hasattr(value, "read"):  # Oracle LOB
        value = value.read()
    return str(value)


def _time(value: Any) -> datetime:
    if isinstance(value, datetime):
        return normalize_timestamp(value)  # naive values (MySQL datetime) are UTC
    return parse_timestamp(_text(value))  # SQLite stores text


# ---------------------------------------------------------------------------------------------------------------------
# Connections from URLs


def connection_factory(url: str, read_only: bool = True) -> tuple[Callable[[], Any], str]:
    """Returns ``(factory, dialect)`` for a URL. Each call of ``factory`` opens a new connection.

    Supported: ``sqlite:///path/to/file.db``, ``postgresql://user:pass@host:5432/db``, ``mysql://user:pass@host/db``,
    ``mssql://user:pass@host:1433/db``. Drivers other than SQLite are optional extras:
    ``pip install "provance[postgres]"``, ``[mysql]``, ``[sqlserver]``.
    """
    parsed = urlparse(url)
    scheme = parsed.scheme.lower()

    if scheme == "sqlite":
        path = unquote(url[len("sqlite://"):])
        if path.startswith("/") and len(path) > 2 and path[2] == ":":  # sqlite:///C:/path on Windows
            path = path[1:]
        if not path:
            raise ValueError("SQLite URL must contain a file path, for example sqlite:///audit.db")
        file = Path(path)
        if read_only:
            if not file.exists():
                raise FileNotFoundError(f"SQLite database not found: {file}")
            target = f"{file.resolve().as_uri()}?mode=ro"
            return (lambda: sqlite3.connect(target, uri=True, timeout=30)), "sqlite"
        return (lambda: sqlite3.connect(str(file), timeout=30)), "sqlite"

    if scheme in ("postgres", "postgresql"):
        psycopg = _optional("psycopg", "postgres")
        target = url.replace("postgres://", "postgresql://", 1)
        return (lambda: psycopg.connect(target, autocommit=read_only)), "postgresql"

    if scheme == "mysql":
        pymysql = _optional("pymysql", "mysql")
        kwargs = dict(host=parsed.hostname or "localhost", port=parsed.port or 3306,
                      user=unquote(parsed.username or ""), password=unquote(parsed.password or ""),
                      database=parsed.path.lstrip("/"), charset="utf8mb4")
        return (lambda: pymysql.connect(**kwargs)), "mysql"

    if scheme in ("mssql", "sqlserver"):
        pymssql = _optional("pymssql", "sqlserver")
        kwargs = dict(server=parsed.hostname or "localhost", port=str(parsed.port or 1433),
                      user=unquote(parsed.username or ""), password=unquote(parsed.password or ""),
                      database=parsed.path.lstrip("/"))
        return (lambda: pymssql.connect(**kwargs)), "sqlserver"

    raise ValueError(f"Unsupported database URL scheme '{parsed.scheme}'. Use sqlite, postgresql, mysql or mssql.")


def connect(url: str) -> tuple[Any, str]:
    """Opens one connection for reading (SQLite files are opened read-only). Returns ``(connection, dialect)``."""
    factory, dialect = connection_factory(url, read_only=True)
    return factory(), dialect


def _optional(module: str, extra: str) -> Any:
    try:
        return __import__(module)
    except ImportError:
        raise RuntimeError(f"The {module} driver is not installed. Install it with: pip install \"provance[{extra}]\"") from None
