# Provance.Core

Tamper-evident audit trail for .NET. Every entry is chained to the previous one with SHA-256 and signed,
so an entry edited, deleted or reordered directly in the database is detected, and verification tells you
exactly where.

Audit tables can usually be edited by the application, its admins and anyone with database access.
PROVANCE lets you prove to someone outside your team (an auditor, a customer, a partner) that the log was
not changed since a given point.

## What it detects

| Situation | Detected |
| :-------- | :------- |
| A stored field of an entry is modified | Yes |
| An entry is deleted, inserted or moved in the middle | Yes |
| Hashes recomputed after an edit, without the signing key | Yes (signature) |
| Entries removed from the end | Only with a trusted checkpoint |
| Full rewrite by someone holding the signing key | Only with a trusted checkpoint |

A checkpoint is a signed "the ledger was at sequence N with hash H". Keep it where the people who can
modify the ledger cannot (storage with retention lock, another team's system, a public page).

## Quick start

```bash
dotnet add package Provance.Core
dotnet add package Provance.Storage.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # or the EF Core provider of your database
```

```csharp
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))   // LedgerId, SigningKeyId, SecretKey
    .UseEntityFrameworkCore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("Audit")));
```

```json
"Provance": { "LedgerId": "orders-api-prod", "SigningKeyId": "k1" }
```

`Provance:SecretKey` (32 bytes or more) comes from a secret store or the `Provance__SecretKey` environment
variable, never from `appsettings.json`. The application refuses to start without it.

## Recording and verifying

```csharp
await ledger.AddEntryAsync("ROLE_GRANTED", new AuditedPayload
{
    ActorId = adminId,
    Description = "Partner access granted",
    Data = new Dictionary<string, object?> { ["partner"] = partnerId, ["role"] = "reader" }
});

var result = await ledger.VerifyAsync();                         // IsValid, Failure, FailedAtSequence, ...
var checkpoint = await ledger.CreateCheckpointAsync();           // store it somewhere safe
var strict = await ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });
```

Any serializable object can be a payload. Write amounts and large numbers as strings.

## Also included

* Several application instances can write to the same ledger, without locks.
* Payload redaction for GDPR erasure requests, without breaking the chain.
* Key rotation, and an `ILedgerSigner` interface for keys held in a KMS.
* Automatic checkpoints to any sink (`AddCheckpointSink`, `AddFileCheckpoints`).
* A published format with test vectors, so a ledger can be verified outside .NET.

## Related packages

* [Provance.Storage.EntityFrameworkCore](https://www.nuget.org/packages/Provance.Storage.EntityFrameworkCore): SQL Server, PostgreSQL, SQLite, MySQL, MariaDB, Oracle, MongoDB
* [Provance.AspNetCore.Middleware](https://www.nuget.org/packages/Provance.AspNetCore.Middleware): one entry per HTTP request

Documentation, format specification and limits: https://github.com/h-ibrahim365/ProvanceProtocol
