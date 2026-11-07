<p align="center">
  <img src="./icons/provanceProtocol.png" alt="PROVANCE logo" width="120" />
</p>

<h1 align="center">PROVANCE</h1>

<p align="center">Tamper-evident audit trail for .NET, Java and Python applications.</p>

<p align="center">
  <a href="https://github.com/h-ibrahim365/ProvanceProtocol/actions/workflows/ci.yml"><img src="https://github.com/h-ibrahim365/ProvanceProtocol/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://www.nuget.org/packages/Provance.Core"><img src="https://img.shields.io/nuget/v/Provance.Core.svg?label=Provance.Core" alt="Provance.Core on NuGet" /></a>
  <a href="https://central.sonatype.com/artifact/io.github.h-ibrahim365/provance-core"><img src="https://img.shields.io/maven-central/v/io.github.h-ibrahim365/provance-core.svg?label=provance-core" alt="provance-core on Maven Central" /></a>
  <a href="https://pypi.org/project/provance/"><img src="https://img.shields.io/pypi/v/provance.svg?label=provance" alt="provance on PyPI" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT license" /></a>
</p>

PROVANCE records application events (who did what, when) in an append only ledger where every entry
is chained to the previous one with SHA-256 and signed. If someone edits, deletes or reorders entries
directly in the database, verification tells you exactly where.

It is a library, not a service: entries live in your own database (PostgreSQL, SQL Server, MySQL, SQLite,
Oracle, ...), and verification runs in your application or in a separate audit tool.

| Implementation | Packages | Status |
| :------------- | :------- | :----- |
| [.NET 8+](src/dotnet/README.md) | `Provance.Core`, `Provance.Storage.EntityFrameworkCore`, `Provance.AspNetCore.Middleware` on NuGet | Released |
| [Java 17+](src/java/README.md) | `provance-core`, `provance-jdbc`, `provance-spring-boot-starter` on Maven Central (`io.github.h-ibrahim365`) | Released |
| [Python 3.10+](src/python/README.md) | `provance` on PyPI: ledger, SQL store, FastAPI, Flask and Django integration, `provance verify` command | Released |

All three follow the same [ledger format](docs/SPEC.md) and pass the same [test vectors](docs/test-vectors/): a
ledger written in one language can be verified, and continued, in the others. The `provance verify` command of the
Python package lets an auditor or a security team check any of them without installing .NET or Java.

## Try it in 2 minutes (.NET)

```bash
dotnet new web -n AuditDemo && cd AuditDemo
dotnet add package Provance.Core
dotnet add package Provance.Storage.EntityFrameworkCore
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet user-secrets init
dotnet user-secrets set "Provance:SecretKey" "any-random-string-of-at-least-32-characters"
```

Replace `Program.cs` with:

```csharp
using Microsoft.EntityFrameworkCore;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"), o => o.LedgerId = "demo")
    .UseEntityFrameworkCore(db => db.UseSqlite("Data Source=audit.db"));
var app = builder.Build();

app.MapPost("/grant/{user}", (string user, ILedgerService ledger) =>
    ledger.AddEntryAsync("ROLE_GRANTED", new AuditedPayload { ActorId = "admin", Description = $"reader role for {user}" }));
app.MapGet("/verify", (ILedgerService ledger) => ledger.VerifyAsync());

app.Run();
```

Then:

```bash
dotnet run --urls http://localhost:5000
curl -X POST http://localhost:5000/grant/alice
curl -X POST http://localhost:5000/grant/bob
curl http://localhost:5000/verify      # "isValid": true

# edit the first entry directly in the database, like someone with database access would
sqlite3 audit.db "UPDATE provance_ledger SET event_type = 'ROLE_REVOKED' WHERE seq = 1"
curl http://localhost:5000/verify      # "isValid": false, "failedAtSequence": 1
```

The table is created on first use. Any SQLite tool works in place of `sqlite3`. For Java and Spring Boot, see
the [Java README](src/java/README.md).

## Why

Audit logs usually live in a table that the application, its admins and anyone who gets database access
can edit. When an auditor, a customer or a court asks "can you prove this log was not changed?", a normal
table cannot answer.

With PROVANCE you can hand the ledger and a checkpoint to someone outside your team, and they can check,
with their own tool, that no entry was modified, removed or inserted since the checkpoint.
Typical uses: access rights granted to external partners, admin actions, changes to financial or
contractual data, deletion requests.

PROVANCE does not stop someone who already has access from editing the database. It makes the edit
visible, and it tells you from which entry the log can no longer be trusted.

## What it detects, and what it does not

| Situation | Detected |
| :-------- | :------- |
| A stored field of an entry is modified (payload, event type, time, actor, ...) | Yes |
| An entry is deleted, inserted or moved in the middle of the ledger | Yes |
| Someone recomputes the hashes after editing, without the signing key | Yes (signature) |
| Entries are removed from the end of the ledger | Only with a trusted checkpoint |
| Someone with the signing key and write access rewrites the whole ledger | Only with a trusted checkpoint |
| The whole database is deleted | No. Use backups. |

A **checkpoint** is a small signed statement "the ledger was at sequence N with hash H". It only helps if
you store it where the people who can modify the ledger cannot: object storage with a retention lock,
WORM storage, a timestamping service, a system owned by another team. PROVANCE can publish checkpoints
automatically; where they go is your decision.

The default signature is HMAC-SHA256. Whoever has the key can sign, so keep it in a secret store and
restrict who can read it, or plug in a signer backed by your KMS so the application never sees the key.

## Using it

The setup guides are per language:

* [.NET](src/dotnet/README.md): packages, configuration, databases, business events, verification
* [Java](src/java/README.md): Spring Boot starter, plain Java, databases, verification
* [Python](src/python/README.md): ledger, SQL store, FastAPI, Flask and Django integration, `provance verify`
  from the command line, offline exports

In all three, the application refuses to start with a missing ledger id, a short or placeholder key, or no store.
There is no silent fallback to an in-memory store.

## Several instances

Several instances of the same application can write to the same ledger. The store rejects a sequence that
already exists; the instance that loses reloads the head and retries. There is no lock and no leader.

Appends to one ledger are serialized by design (each entry depends on the previous one). Entries are
batched under load. If one ledger becomes a bottleneck, use one ledger per tenant or per domain.

## Personal data

A ledger is append only, which conflicts with the right to erasure. PROVANCE helps in three ways:

* the HTTP integrations (ASP.NET Core middleware, Spring filter, Python ASGI, WSGI and Django middleware) do
  not record query strings, IP addresses, user agents or exception messages unless you enable them;
* only the payload hash is chained, so a payload can be redacted (`RedactPayloadAsync` in .NET,
  `redactPayload` in Java, `redact_payload` in Python) and the ledger still verifies (the entry, its type and
  its time remain);
* you can keep personal data out of payloads entirely and store a reference instead.

## Compared with other options

* **SQL Server 2022 / Azure SQL ledger tables** do this inside the database, with digests sent to
  immutable storage. If your data is already there, they are probably the simpler choice.
* **immudb** and similar databases give stronger guarantees but are a separate system to run.
* **PROVANCE** is for cases those do not cover:
  * your database has no ledger feature (PostgreSQL, MySQL, SQLite, ...), or you want the same audit trail on several databases;
  * you want to audit business events you choose (an order refunded, a role granted), not row changes;
  * the ledger must be verifiable outside the database, by an auditor with read access and the
    verification key, using the published format and test vectors;
  * several instances of your application write to the same ledger.

## Documentation

* [Ledger format specification](docs/SPEC.md) and [test vectors](docs/test-vectors/) (with an independent Python checker)
* [.NET guide](src/dotnet/README.md) and [implementation notes](docs/ARCHITECTURE.md)
* [Java guide](src/java/README.md)
* [Python guide](src/python/README.md)
* [Migration from 0.0.3](docs/MIGRATION.md) (.NET)
* [Changelog](CHANGELOG.md)
* [Security policy](SECURITY.md)

## Status

Version 0.1.0. The ledger format is versioned (format 1) and covered by test vectors; the .NET, Java and
Python APIs may still change before 1.0.

## Roadmap for 0.2

Planned, in no fixed order. Feedback on what matters most to you is welcome in the issues.

* **Several ledgers in one application**, for example one per tenant, or one for audit and one for billing
  (keyed services in .NET, named beans in Spring).
* **Asynchronous mirror to a second store**, for example an archive database or a database owned by another
  team, where the application account can only insert. Each entry verifies on its own, so the copy can be
  checked independently.
* **Field-level diff between the two copies.** Verification says which entry was changed; it cannot say what
  the original value was, because the ledger only keeps its hash. With a mirror, the copy that still verifies
  is the original, and a diff shows the changed fields with their original and current values
  (`provance diff` in the command line tool).
* **ECDSA signer**, so that an auditor can verify a ledger with the public key only.
* **Querying** entries by actor, event type or period, with the matching indexes.
* **Health checks and metrics** (OpenTelemetry).
* **MongoDB in the .NET test suite.**
* **Cross-language tests in every suite**: the Python suite already verifies a ledger written by Java
  ([fixture](docs/test-vectors/ledger-java/)); the .NET and Java suites will read it too.

## License

MIT, see [LICENSE](LICENSE). If PROVANCE is useful to you, you can [sponsor the project](https://github.com/sponsors/h-ibrahim365).
