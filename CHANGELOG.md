# Changelog

All notable changes are listed here. The project follows semantic versioning; while the version is 0.x,
a minor version may contain breaking changes.

## Unreleased

### Security review fixes (Java 0.1.1, Python 0.1.1, and .NET 0.2.0)

From an external review of the code.

* Python: `add_entry_async` no longer blocks the event loop when the queue is full (`WAIT` behavior). It waits for
  room asynchronously, so a timeout around it, such as the middleware's `audit_timeout`, also bounds that wait.
  Before, a full queue (for example during a slow database) froze every request of the worker.
* Python and Java: an entry submitted at the exact moment the ledger was closing could be accepted after the writer
  had stopped and never complete. Accepting entries and stopping the writer are now synchronized.
* .NET, Java and Python: checkpoint files are written to a temporary file, then linked into place, so a crash can no
  longer leave a truncated checkpoint that blocks that sequence forever. An existing file that does not parse is
  replaced, and reading the latest checkpoint skips such a file.
* .NET 0.2 (Java and Python with their 0.2): an erasure through the library appends a signed `provance.redaction`
  record naming the erased entry, and verification fails with `UnrecordedRedaction` when a payload is missing without
  one. Before, `UPDATE ... SET payload = NULL` in the database looked like a legitimate GDPR erasure. Ledgers erased
  with 0.1 verify with `AcceptUnrecordedRedactions = true`. See docs/SPEC.md, section 6.

### .NET 0.2.0 (in progress; Java and Python follow, the three ship together)

Ledger format 1 is unchanged: a ledger written by 0.1 verifies with 0.2. Retention adds a reserved event type
(`provance.retention`, docs/SPEC.md section 9) that verifiers must know before reading a store that used retention.

* Several ledgers in one application: `AddProvance(name, configuration)` and `AddProvance(name, configure)`, each
  with its own options, store, keys, archive and checkpoints, injected as a keyed service
  (`[FromKeyedServices("billing")] ILedgerService`). `AddHttpAuditing` on a named ledger's builder sends the HTTP
  entries there (`HttpAuditOptions.LedgerName`).
* Archive copy: `UseArchive(store)`, `UseEntityFrameworkCoreArchive(db => ...)`. Every entry is copied to a second
  store in the background, after being checked; an entry modified before it was copied is not propagated and
  `GetArchiveStatus()` reports it. `RedactPayloadAsync` erases the payload in both copies and throws
  `LedgerArchiveException` if the archive refuses.
* Field-level comparison: `CompareWithArchiveAsync()` and `LedgerComparer` list, for every changed entry, the fields
  that differ with their original and current values, and which copy still verifies (docs/ARCHIVE.md).
* Retention: `Retention:KeepFor` (or `ApplyRetentionAsync`) removes old entries from the primary store once the
  archive holds an identical copy, after appending a signed retention record; `VerifyAsync` accepts the shortened
  ledger and still detects entries deleted without such a record (docs/RETENTION.md). `IPrunableLedgerStore` is
  implemented by the in-memory and Entity Framework Core stores.
* `LedgerJsonLines` and `JsonLinesLedgerFile`: export and import in the JSON Lines format of the Java and Python
  libraries, to verify or compare an export.
* `LedgerVerificationResult.FirstSequence`.
* `LedgerCheckpoint.ToJson()` writes `createdAt` as `2026-11-07T09:00:06.789Z`, like Java and Python, instead of
  `+00:00`. `FromJson` reads both. The demo site's verifier now accepts both too.
* Event types starting with `provance.` are reserved and rejected by `AddEntryAsync`.
* Cross-language fixtures: ledgers written by the Python and .NET libraries (docs/test-vectors/ledger-python,
  ledger-dotnet) next to the Java one; the .NET suite verifies all three.
* `ILedgerService` gained members (with default implementations) and `IProvanceBuilder` gained `LedgerName`.
  Custom implementations of these interfaces keep compiling.

* POM metadata: the project and SCM URLs of the modules pointed to `.../ProvanceProtocol/provance-core` (and so on),
  which does not exist. They now point to the repository. No code change.
* Release tags: from 0.2.0 the .NET packages are released from a tag `dotnet-vX.Y.Z`, like `java-vX.Y.Z` and
  `python-vX.Y.Z`. The `vX.Y.Z` tag of 0.1.0 stays as it is and is still accepted.
* CI runs only the jobs for the parts a push or pull request changes. Changes to `docs/test-vectors/` or to the
  workflow run everything.

## Python 0.1.0

First Python implementation of ledger format 1, in `src/python`. Python 3.10 or later. Same behavior as the .NET
and Java 0.1.0 libraries.

* `ProvanceLedger`: append entries (`add_entry`, `add_entry_async`, `submit`), verify, create checkpoints, redact
  payloads. Background writer with batching, bounded retries, several instances per ledger, resync after an
  ambiguous failure, drain on close. Refuses to start without a ledger id, a strong key or a store; raises
  instead of hanging when used after `fork()`.
* `SqlLedgerStore`: SQLite, PostgreSQL, MySQL, MariaDB and SQL Server through DB-API drivers. Same table, columns
  and value formats as the .NET Entity Framework Core store and the Java JDBC store.
* `provance.http`: one entry per HTTP request for ASGI (FastAPI, Starlette), WSGI (Flask) and Django, with the same
  payload and privacy defaults as the ASP.NET Core middleware and the Spring filter.
* Payloads: dicts, dataclasses, Pydantic models, `AuditedPayload` and plain objects, serialized like in .NET and
  Java.
* Checkpoints: file sink with the .NET and Java layout, custom sinks, automatic publication.
* `provance verify` and `provance export`: verify a ledger from a database or a JSON Lines export, with current and
  retired keys and an optional trusted checkpoint. Exit code 0, 1 or 2 for scheduled checks.
* Canonical JSON from the independent `rfc8785` package. Passes the shared test vectors, verifies a ledger written
  by the Java library (`docs/test-vectors/ledger-java`), and ledgers written in Python verify and continue in Java.
  Tested on Python 3.10 to 3.13, with SQLite and PostgreSQL.

## Java 0.1.0

First Java implementation of ledger format 1, in `src/java`. Java 17 or later.

* `provance-core`: ledger, writer, verification, checkpoints, HMAC signer, RFC 8785 canonicalization. No
  dependencies. Same behavior as the .NET 0.1.0 core: several instances per ledger, resync after an ambiguous
  failure, batching, bounded retries, drain on close.
* `provance-jdbc`: store for PostgreSQL, SQL Server, MySQL, MariaDB, Oracle, SQLite, H2 and generic SQL. Same table
  and column names as the .NET Entity Framework Core store.
* `provance-spring-boot-starter`: Spring Boot 4 auto-configuration from `provance.*` properties and an optional HTTP
  audit filter with the same privacy defaults as the ASP.NET Core middleware.
* Passes the shared test vectors. Tested in CI on PostgreSQL, SQL Server, MySQL, H2 and SQLite.

## 0.1.0

First release of ledger format 1. Not compatible with ledgers written by 0.0.x, see [docs/MIGRATION.md](docs/MIGRATION.md).

### Security

* Payloads are serialized with their runtime type. In 0.0.3, fields of derived types (for example the
  path, method and IP recorded by the middleware) were stored but not covered by the hash.
* Canonical JSON follows RFC 8785. Published test vectors, checked in CI by an independent Python implementation.
* The signed envelope includes the ledger id, the format version and the key id: entries cannot be moved
  between ledgers, and keys can be rotated.
* Hash and signature are separate: the chain is a SHA-256 chain, each entry is signed through `ILedgerSigner`.
  HMAC-SHA256 remains the default; KMS backed signers can be plugged in.
* Signed checkpoints and checkpoint sinks. Verification against a trusted checkpoint detects truncation
  and full rewrites by someone holding the key.
* The default insecure key is gone. Missing, short (under 32 bytes) and placeholder keys are refused at startup.

### Reliability

* Lease based single writer replaced by optimistic concurrency on the store's unique sequence. Several
  application instances can write to the same ledger.
* A failed or ambiguous append no longer blocks the writer: it reloads the head, acknowledges what was
  persisted and reseals the rest.
* Appends are batched.
* Retries are short, bounded and configurable. Polly is no longer a dependency.
* On shutdown the writer drains its queue; entries left after the timeout fail instead of hanging.
* Queue capacity and overload behavior (wait or fail fast) are configurable.

### ASP.NET Core middleware

* Failure mode: log and continue (default) or throw. Audit wait bounded by `AuditTimeout`.
* Query string, client IP and user agent are opt-in. Exception messages are not recorded.
* Status code is 500 when an exception escapes. Actor id comes from `NameIdentifier` or `sub` only.
* Excluded paths (`/health`, `/metrics`, ...) and a custom filter.
* Uses the ASP.NET Core shared framework instead of `Microsoft.AspNetCore.Http.Abstractions` 2.x.

### Storage

* New `Provance.Storage.EntityFrameworkCore`: one store for every EF Core provider (SQL Server, PostgreSQL,
  SQLite, MySQL, MariaDB, Oracle, ...). Tested on SQLite, PostgreSQL and SQL Server. Table created on first
  use, or mapped into your own `DbContext` with `AddProvanceLedger()`.
* `Provance.Storage.MongoDB` is deprecated and no longer developed. MongoDB goes through the same store with
  the `MongoDB.EntityFrameworkCore` provider (collection and unique index created on first use).
* Payload stored as canonical JSON text, so hashes survive the round trip on every database.

### API

* Immutable `LedgerEntry` and `LedgerCheckpoint` records.
* `VerifyAsync` returns `LedgerVerificationResult` and streams entries; `ThrowOnFailure` raises `LedgerTamperedException`.
* Payload redaction (`RedactPayloadAsync`) that keeps the chain verifiable.
* `AddProvance(...)` registration in `Provance.Core`, with `UseEntityFrameworkCore`, `UseInMemoryStore`, `UseStore`,
  `UseSigner`, `AddCheckpointSink`, `AddFileCheckpoints`, `AddHttpAuditing`.
* No silent fallback to the in-memory store.
* Internal plumbing (`LedgerService`, writer, queue) is no longer public.

### Build

* Shared `Directory.Build.props`, single version number. Source Link comes from the .NET SDK (no extra package).
* CI on every push and pull request. Release workflow checks that the tag matches the version, runs the
  tests, then publishes.

## 0.0.3

Single writer with a lease, `Sequence` ordering. Superseded by 0.1.0.
