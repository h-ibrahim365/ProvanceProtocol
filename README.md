<p align="center">
  <img src="./icons/provanceProtocol.png" alt="PROVANCE logo" width="120" />
</p>

<h1 align="center">PROVANCE</h1>

<p align="center">Tamper-evident audit trail for .NET applications.</p>

<p align="center">
  <a href="https://github.com/h-ibrahim365/ProvanceProtocol/actions/workflows/ci.yml"><img src="https://github.com/h-ibrahim365/ProvanceProtocol/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://www.nuget.org/packages/Provance.Core"><img src="https://img.shields.io/nuget/v/Provance.Core.svg?label=Provance.Core" alt="Provance.Core on NuGet" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT license" /></a>
</p>

PROVANCE records application events (who did what, when) in an append only ledger where every entry
is chained to the previous one with SHA-256 and signed. If someone edits, deletes or reorders entries
directly in the database, verification tells you exactly where.

It is a library, not a service: entries live in your own database (SQL Server, PostgreSQL, SQLite,
MySQL, Oracle, ... through Entity Framework Core, or any store you implement), and verification runs
in your application or in a separate audit tool.

## Try it in 2 minutes

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

The table is created on first use. Any SQLite tool works in place of `sqlite3`.

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

## Setting it up in your application

Three packages:

| Package | Use |
| :------ | :-- |
| [`Provance.Core`](https://www.nuget.org/packages/Provance.Core) | Ledger, verification, checkpoints. Works in any .NET 8+ host. |
| [`Provance.Storage.EntityFrameworkCore`](https://www.nuget.org/packages/Provance.Storage.EntityFrameworkCore) | Store for any database with an EF Core provider. |
| [`Provance.AspNetCore.Middleware`](https://www.nuget.org/packages/Provance.AspNetCore.Middleware) | One audit entry per HTTP request. |

```bash
dotnet add package Provance.Core
dotnet add package Provance.Storage.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # or the EF Core provider of your database
dotnet add package Provance.AspNetCore.Middleware
```

`appsettings.json` (no secret here):

```json
{
  "Provance": {
    "LedgerId": "orders-api-prod",
    "SigningKeyId": "k1"
  },
  "ConnectionStrings": {
    "Audit": "Host=localhost;Database=audit;Username=app"
  }
}
```

The key comes from a secret store or an environment variable, at least 32 bytes:

```bash
# development
dotnet user-secrets set "Provance:SecretKey" "$(openssl rand -base64 48)"
# production: environment variable Provance__SecretKey, Key Vault, etc.
```

`Program.cs`:

```csharp
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("Audit")))
    .AddHttpAuditing();          // optional: one entry per HTTP request

var app = builder.Build();

app.UseAuthentication();
app.UseProvanceAuditing();       // after authentication, so the actor is known
```

The application refuses to start with a missing ledger id, a short key or a placeholder key, and there is
no silent fallback to an in-memory store.

## Databases

| Database | How | Tested in CI |
| :------- | :-- | :----------- |
| PostgreSQL | `db.UseNpgsql(...)` | Yes |
| SQL Server, Azure SQL | `db.UseSqlServer(...)` | Yes |
| SQLite | `db.UseSqlite(...)` | Yes |
| MySQL, MariaDB | `db.UseMySql(...)` (Pomelo provider) | No, same code path |
| Oracle | `db.UseOracle(...)` | No, same code path |
| MongoDB | `MongoDB.EntityFrameworkCore` provider, with your own context | No, see the [store README](src/dotnet/ProvanceProtocol/Provance.Storage.EntityFrameworkCore/README.md) |
| Anything else | implement `ILedgerStore` (5 methods) | Contract in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |

The ledger can have a table of its own, created on first use, or live in your own `DbContext` with
`modelBuilder.AddProvanceLedger()` and your migrations.

## Recording business events

The HTTP middleware tells you that `POST /orders` returned 201. Most audits need more than that:

```csharp
app.MapPost("/orders/{id}/refund", async (Guid id, RefundRequest request, ILedgerService ledger, ClaimsPrincipal user) =>
{
    await ledger.AddEntryAsync("ORDER_REFUNDED", new AuditedPayload
    {
        ActorId = user.FindFirstValue(ClaimTypes.NameIdentifier),
        Description = "Refund issued",
        Data = new Dictionary<string, object?> { ["orderId"] = id, ["amount"] = request.Amount }
    });

    // ... do the refund
});
```

Any serializable object can be a payload, and its runtime type is used, so derived types are fully signed.
Write money amounts and large numbers as strings: payloads are canonicalized with RFC 8785, which treats
numbers as doubles.

`AddEntryAsync` returns once the entry is stored. Call it before the operation if the operation must not
happen without an audit trace.

## Verifying

```csharp
var result = await ledger.VerifyAsync();
// result.IsValid, result.Failure, result.FailedAtSequence, result.LastSequence, result.LastHash
```

With a checkpoint obtained from your trusted storage:

```csharp
var checkpoint = LedgerCheckpoint.FromJson(jsonFromTrustedStorage);
var result = await ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });
```

Automatic checkpoints:

```csharp
builder.Services.AddProvance(...)
    .UseEntityFrameworkCore(...)
    .AddFileCheckpoints("/mnt/immutable/provance");   // or AddCheckpointSink(yourSink)
```

`LedgerVerifier` can also be used on its own, for example in a command line tool run by an auditor with
read access to the database and the verification key.

## Several instances

Several instances of the same application can write to the same ledger. The store rejects a sequence that
already exists; the instance that loses reloads the head and retries. There is no lock and no leader.

Appends to one ledger are serialized by design (each entry depends on the previous one). Entries are
batched under load. If one ledger becomes a bottleneck, use one ledger per tenant or per domain.

## Personal data

A ledger is append only, which conflicts with the right to erasure. PROVANCE helps in three ways:

* the HTTP middleware does not record query strings, IP addresses, user agents or exception messages
  unless you enable them;
* only the payload hash is chained, so `RedactPayloadAsync(id)` can delete a payload and the ledger still
  verifies (the entry, its type and its time remain);
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
* [.NET implementation notes](docs/ARCHITECTURE.md)
* [Migration from 0.0.3](docs/MIGRATION.md)
* [Changelog](CHANGELOG.md)
* [Security policy](SECURITY.md)

## Status

Version 0.1.0. The ledger format is versioned (format 1) and covered by test vectors; the .NET API may
still change before 1.0.

Planned next: health checks and metrics, a PostgreSQL store, a KMS signer example, a verification command line tool.

## License

MIT, see [LICENSE](LICENSE). If PROVANCE is useful to you, you can [sponsor the project](https://github.com/sponsors/h-ibrahim365).
