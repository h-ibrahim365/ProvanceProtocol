# PROVANCE for Python

Python implementation of PROVANCE, the tamper-evident audit trail. It writes the same ledger format as the
[.NET](https://github.com/h-ibrahim365/ProvanceProtocol/blob/main/src/dotnet/README.md) and
[Java](https://github.com/h-ibrahim365/ProvanceProtocol/blob/main/src/java/README.md) libraries
([format specification](https://github.com/h-ibrahim365/ProvanceProtocol/blob/main/docs/SPEC.md)) and passes the
same test vectors: a ledger written in one language can be verified, and continued, in the others.

To see what it does before installing anything, try the [live demo](https://provance.iamhakim.com).

It also ships the `provance verify` command, so an auditor or a security team can check a ledger written by any of
the three without installing .NET or Java.

Canonical JSON comes from the [`rfc8785`](https://pypi.org/project/rfc8785/) package, an RFC 8785 implementation
independent of the .NET and Java code.

Python 3.10 or later.

## Install

```bash
pip install provance                  # SQLite, in-memory store, JSON Lines exports
pip install "provance[postgres]"      # + PostgreSQL (psycopg)
pip install "provance[mysql]"         # + MySQL, MariaDB (PyMySQL)
pip install "provance[sqlserver]"     # + SQL Server, Azure SQL (pymssql)
```

## Writing entries

```python
import os
from provance import AuditedPayload, ProvanceLedger, SqlLedgerStore

ledger = ProvanceLedger(
    ledger_id="orders-api-prod",
    secret_key=os.environ["PROVANCE_SECRET_KEY"],
    store=SqlLedgerStore.from_url("postgresql://app@db.internal/audit"),
)

ledger.add_entry("ORDER_REFUNDED", AuditedPayload(
    actor_id=user_id,
    description="Refund issued",
    data={"orderId": order_id, "amount": "49.90"},
))

ledger.close()  # on shutdown: writes the queued entries
```

`add_entry` returns once the entry is stored, with its sequence, hash and signature. `await ledger.add_entry_async(...)`
does the same in asyncio code, and `ledger.submit(...)` returns a `concurrent.futures.Future` without waiting.

The ledger refuses to start with a missing ledger id, an HMAC key shorter than 32 bytes or a placeholder, or no
store. There is no silent fallback to an in-memory store.

Create one `ProvanceLedger` per ledger and per process, and close it on shutdown (or use it as a context manager).
A background thread writes the entries; it does not survive `fork()`, so with gunicorn create the ledger in each
worker (in the `post_fork` hook, or lazily on first use), not before the fork with `--preload`. A ledger used after
a fork raises `LedgerUnavailableError` instead of hanging.

### Payloads

A payload can be a `dict`, a dataclass, a Pydantic model, an `AuditedPayload` or any object with public
attributes. `None` attributes are omitted, as in .NET and Java. Enums, `datetime`, `UUID`, `Decimal` and `bytes`
(Base64) are converted; for anything else, add a `__provance_payload__()` method or pass
`payload_serializer=` to the ledger.

Every field of the payload is covered by the hash. A payload that cannot be serialized raises `PayloadError` before
anything is queued.

Numbers are stored as IEEE 754 doubles, as RFC 8785 requires: write money amounts and integers above 2^53 as
strings.

### Settings

Settings have the same names (in snake_case) and defaults as in .NET and Java. Pass them as keyword arguments or as
a `ProvanceOptions` object.

| Setting | Default | Meaning |
| :------ | :------ | :------ |
| `ledger_id` | required | stable identifier, for example `orders-api-prod` |
| `secret_key` | required | HMAC secret of the current key, at least 32 bytes |
| `signing_key_id` | `k1` | id of the current key; change it on every rotation |
| `retired_keys` | `{}` | old keys (id to secret), to verify entries signed before a rotation |
| `queue_capacity` | `10000` | entries waiting to be written |
| `queue_full_behavior` | `WAIT` | `WAIT` blocks the caller, `FAIL_FAST` raises `LedgerOverloadedError` |
| `max_batch_size` | `256` | entries written in one transaction |
| `max_append_attempts` | `5` | attempts on a transient store error before callers get `LedgerUnavailableError` |
| `checkpoint_interval` | 5 minutes | interval of automatic checkpoints, when a sink is configured |
| `shutdown_timeout` | 30 seconds | how long `close()` waits for the queue to drain |

### Key rotation

```python
ledger = ProvanceLedger(
    ledger_id="orders-api-prod",
    signing_key_id="k2",
    secret_key=os.environ["PROVANCE_KEY_K2"],
    retired_keys={"k1": os.environ["PROVANCE_KEY_K1"]},
    store=store,
)
```

New entries are signed with `k2`; entries signed with `k1` still verify. To keep the key out of the application,
pass `signer=` with an object backed by your KMS: a `key_id` property, `sign(digest)` returning the signature
bytes, and `verify(key_id, digest, signature)` returning a `SignatureCheck` (see `provance.LedgerSigner`).

## One entry per HTTP request

`provance.http` records an `HTTP_REQUEST_COMPLETED`, `HTTP_REQUEST_FAILED` or `HTTP_REQUEST_ABORTED` entry after
each response, with the method, path, status code, duration, trace id and actor. Same payload and privacy defaults
as the ASP.NET Core middleware and the Spring filter: query strings, client IPs and user agents are off unless you
enable them, and exception messages are never recorded (only the exception type). `/health`, `/healthz`,
`/metrics` and `/favicon.ico` are skipped.

FastAPI, Starlette and other ASGI applications:

```python
from provance.http import ProvanceAsgiMiddleware

app.add_middleware(ProvanceAsgiMiddleware, ledger=ledger)
```

Flask and other WSGI applications (the entry is written when the server closes the response, so the status code is
final; with Flask's test client, close the response or use `with client.get(...)`):

```python
from provance.http import ProvanceWsgiMiddleware

app.wsgi_app = ProvanceWsgiMiddleware(app.wsgi_app, ledger)
```

Django:

```python
# myproject/audit.py
from provance.http import django_middleware
audit_middleware = django_middleware(ledger)

# settings.py, after AuthenticationMiddleware
MIDDLEWARE = [..., "myproject.audit.audit_middleware"]
```

Options go in `HttpAuditOptions`: `include_query_string`, `include_client_ip`, `include_user_agent`,
`excluded_paths`, `should_audit(method, path)`, `actor_id_resolver`, `audit_timeout` (seconds) and `failure_mode`.
With the default `LOG_AND_CONTINUE`, a request whose entry cannot be written still gets its response and the error
is logged; `RAISE` raises after the response (it does not undo what the handler did). Record the business events
that matter with `add_entry` in the handler itself, where a failure can stop the operation.

The actor is the authenticated user: `scope["user"]` in Starlette, `REMOTE_USER` in WSGI, `request.user.pk` in
Django. Pass `actor_id_resolver` for anything else.

## Databases

`SqlLedgerStore` uses the same table and columns as the .NET Entity Framework Core store and the Java JDBC store, so
the three libraries can write to the same ledger.

| Database | URL | Tested |
| :------- | :-- | :----- |
| SQLite | `sqlite:///audit.db` | Yes |
| PostgreSQL | `postgresql://user:pass@host/db` | Yes (CI) |
| MySQL, MariaDB | `mysql://user:pass@host/db` | No, same SQL as the Java store |
| SQL Server, Azure SQL | `mssql://user:pass@host/db` | No, same SQL as the Java store |

`from_url` opens a connection per operation. In production, pass a pooled factory instead, for example SQLAlchemy's:

```python
store = SqlLedgerStore(engine.raw_connection, "postgresql")
```

The table is created on first use. If your migrations create it, pass `create_table=False` and take the statements
from `store.create_table_statements()`. The unique index on `seq` is what keeps several writers consistent: do not
drop it.

`InMemoryLedgerStore` is for tests only.

## Several instances

Several processes and machines can write to the same ledger. The store rejects a sequence that already exists; the
instance that loses reloads the head and retries. There is no lock and no leader. Entries are batched under load.

When the database fails after writing a batch but before confirming it, the writer reloads the head, recognizes its
own entries by id and continues the chain without duplicates. Transient failures are retried with backoff
(`max_append_attempts`); after that, the callers get `LedgerUnavailableError` and the writer keeps serving new
entries.

## Verifying and checkpoints

```python
result = ledger.verify()
print(result.valid, result.failure, result.failed_at_sequence, result.message)
```

Verification detects a modified field, a deleted, inserted or moved entry, and hashes recomputed without the key.
Entries removed from the end, and a full rewrite by someone holding the key, are only detected against a checkpoint
stored where those people cannot write.

```python
ledger = ProvanceLedger(..., checkpoint_directory="/mnt/immutable/provance")   # or checkpoint_sinks=[my_sink]

checkpoint = LedgerCheckpoint.from_json(json_from_trusted_storage)
result = ledger.verify(checkpoint)
```

Checkpoints are published at `checkpoint_interval` when the head has moved. A sink is any object with a
`publish(checkpoint)` method. The file sink writes `{directory}/{ledger_id}/{sequence}.json` and never overwrites a
file, like the .NET and Java file sinks; checkpoint JSON is the same in the three languages.

`ledger.redact_payload(entry_id)` removes a payload (for a GDPR erasure request) and the ledger still verifies: only
the payload hash is chained.

## Verifying from the command line

For an auditor with read access to the database and the verification key. The HMAC secret is read from an
environment variable (default `PROVANCE_SECRET_KEY`) or a file, never from the command line, so it does not end up
in your shell history.

```bash
export PROVANCE_SECRET_KEY="..."
provance verify --db "postgresql://auditor@db.internal/audit" --ledger-id orders-api-prod \
                --checkpoint /mnt/immutable/provance
```

```text
OK: 18342 entries verified up to sequence 18342.
Checkpoint at sequence 18300 matched.
Head: sequence 18342, hash 4f1c...
```

| Option | Meaning |
| :----- | :------ |
| `--db URL` | `sqlite:///audit.db` (opened read-only), `postgresql://...`, `mysql://...`, `mssql://...` |
| `--file PATH` | a JSON Lines export instead of a database |
| `--table`, `--schema` | ledger table, default `provance_ledger` |
| `--ledger-id` | the ledger id configured in the application (required) |
| `--key-id`, `--key-env`, `--key-file` | current key id (default `k1`) and where its secret is |
| `--retired-key ID=VAR` | a retired key and the environment variable holding its secret (repeatable) |
| `--checkpoint PATH` | a trusted checkpoint file, or a checkpoint directory (the latest one is used) |
| `--from-checkpoint` | verify only from the checkpoint onward |
| `--json` | machine readable output |

Exit code: `0` valid, `1` verification failed, `2` usage or connection error, so it can run on a schedule and alert
on failure. Use a database account that can only read the ledger table.

To audit offline, export the table once and verify the file anywhere:

```bash
provance export --db "postgresql://auditor@db.internal/audit" -o ledger.jsonl
provance verify --file ledger.jsonl --ledger-id orders-api-prod --checkpoint checkpoint.json
```

The export has one entry per line, with the field names of the .NET and Java `LedgerEntry`.

The same checks are available without a ledger instance, for an audit script:

```python
from provance import HmacVerifier, LedgerVerifier, SqlLedgerReader, connect

connection, dialect = connect("postgresql://auditor@db.internal/audit")   # read only
verifier = LedgerVerifier(SqlLedgerReader(connection, dialect),
                          HmacVerifier({"k2": current_secret, "k1": retired_secret}),
                          ledger_id="orders-api-prod")
result = verifier.verify(checkpoint=checkpoint)
```

## Several ledgers

Create one `ProvanceLedger` per ledger, each with its own `ledger_id`, key and table. The HTTP integrations write to
the ledger you pass them.

```python
billing = ProvanceLedger(ledger_id="billing-prod", secret_key=..., store=SqlLedgerStore.from_url(url, table="provance_ledger_billing"))
access = ProvanceLedger(ledger_id="access-prod", secret_key=..., store=SqlLedgerStore.from_url(url, table="provance_ledger_access"))
app.add_middleware(ProvanceAsgiMiddleware, ledger=access)
```

## Archive copy and comparison (0.2)

```python
ledger = ProvanceLedger(
    ledger_id="orders-api-prod",
    secret_key=os.environ["PROVANCE_SECRET_KEY"],
    store=SqlLedgerStore.from_url(app_url),
    archive=SqlLedgerStore.from_url(archive_url, table="provance_archive"),   # an account that can only insert
)

comparison = ledger.compare_with_archive()
if not comparison.identical:
    log.critical(comparison.describe())
```

Every entry is copied to the archive in a background thread, after being checked: an entry modified before it was
copied is not propagated, and `archive_status()` reports it. When verification fails, the comparison shows which
field changed and its original value:

```
Entry 42 (ORDER_REFUNDED) was changed in the primary store (the archive copy verifies, the primary copy does not).
  payload.data.amount: "49.90" -> "4990.00"
```

From the command line, between two databases or exports:

```bash
provance diff postgresql://auditor@db/app postgresql://auditor@archive/audit --ledger-id orders-api-prod
provance diff primary.jsonl archive.jsonl --ledger-id orders-api-prod
```

`redact_payload` erases the payload in both copies, after appending a signed redaction record: a payload set to
`NULL` directly in the database is reported by `verify()` (`unrecorded_redaction`). Ledgers erased with 0.1 verify
with `accept_unrecorded_redactions=True` (`--accept-unrecorded-redactions` on the command line).

## Retention (0.2)

`retention_keep_for=timedelta(days=7)` removes older entries from the primary store once the archive holds an identical
copy, after appending a signed retention record; `verify()` still passes, and deleting the first entries without such a
record is still detected. Or call `ledger.apply_retention(timedelta(days=7))` from a scheduled job. Needs `DELETE` on
the primary table. See [RETENTION.md](https://github.com/h-ibrahim365/ProvanceProtocol/blob/main/docs/RETENTION.md).

## Status

Version 0.2.0, format 1. Same behavior as the .NET and Java 0.2.0 libraries. Tested on Python 3.10 to 3.13, with
SQLite and PostgreSQL.
