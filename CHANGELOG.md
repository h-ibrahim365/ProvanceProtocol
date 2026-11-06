# Changelog

All notable changes are listed here. The project follows semantic versioning; while the version is 0.x,
a minor version may contain breaking changes.

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
