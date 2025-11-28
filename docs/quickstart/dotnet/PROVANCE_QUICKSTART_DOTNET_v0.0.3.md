<p align="center">
  <img src="/icons/provanceProtocol.png" alt="PROVANCE Protocol logo" width="140" />
</p>

# PROVANCE Quickstart (.NET) — v0.0.3

PROVANCE provides **tamper-evident audit trails** for .NET by chaining events using **HMAC-SHA256**.
In **v0.0.3**, correctness under concurrency is achieved via a **Single Writer** with deterministic ordering (`Sequence`).

---

## 0) Breaking change (v0.0.2 → v0.0.3)

v0.0.3 changes the ordering model and the signed content (adds `Sequence`), so existing ledgers from v0.0.2 are **not compatible**.

✅ **Required migration (recommended):** use a **new collection** (example: `ledger_entries_v1`).

---

## 1) Install

```bash
dotnet add package Provance.Core
dotnet add package Provance.AspNetCore.Middleware
dotnet add package Provance.Storage.MongoDB
```

---

## 2) Configure (`appsettings.json`)

```json
{
  "ProvanceProtocol": {
    "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
    "SecretKey": "CHANGE_ME_IN_PROD"
  },
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries_v1"
  }
}
```

Notes:
- `GenesisHash` must be a 64-char hex string.
- `SecretKey` must be kept private (use a secret manager / KMS in production).
- For v0.0.3, create a **new collection** name (do not reuse v0.0.2 data).

---

## 3) Register services (DI)

```csharp
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Storage.MongoDB.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Storage
builder.Services.AddProvanceMongoStorage(builder.Configuration);

// Core options + services
builder.Services.AddProvanceLogging(options =>
{
    var cfg = builder.Configuration.GetSection("ProvanceProtocol");
    options.GenesisHash = cfg["GenesisHash"] ?? string.Empty;
    options.SecretKey = cfg["SecretKey"] ?? string.Empty;
});

var app = builder.Build();
```

---

## 4) Add the middleware

```csharp
app.UseProvanceLogger();
```

This captures and enqueues audit events during the request pipeline (based on your middleware configuration).

---

## 5) Minimal API endpoints (write + verify)

```csharp
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

app.MapPost("/api/audit", async (ILedgerService ledger, CancellationToken ct) =>
{
    var entry = await ledger.AddEntryAsync(
        eventType: "HTTP_REQUEST",
        payload: new AuditedPayload
        {
            ActorId = "user-123",
            Description = "Example audited event"
        },
        cancellationToken: ct);

    return Results.Ok(new { entry.Id, entry.Sequence, entry.CurrentHash });
});

app.MapGet("/api/ledger/verify", async (ILedgerService ledger, CancellationToken ct) =>
{
    var (ok, reason) = await ledger.VerifyChainIntegrityAsync(ct);
    return ok ? Results.Ok(new { Ok = true, Reason = reason })
              : Results.Conflict(new { Ok = false, Reason = reason });
});

app.Run();
```

---

## 6) What “acknowledged” means in v0.0.3

In v0.0.3, `AddEntryAsync(...)` completes only after the Single Writer has:

1) assigned `Sequence`
2) computed `CurrentHash` (HMAC) using `PreviousHash` + content
3) persisted the entry via `ILedgerStore.WriteEntryAsync`

This provides the strongest correctness mode, but increases end-to-end latency vs “fire-and-forget”.

Outbox + configurable ack levels are planned for v0.0.4+.

---

## 7) MongoDB behavior (v0.0.3)

The MongoDB provider creates on startup:

- ledger collection: your configured `CollectionName` (example: `ledger_entries_v1`)
- lock/lease collection: `provance_locks`

It also creates indexes automatically:

- `(Sequence desc, Id desc)` — fast chain-head lookup
- `(Id asc)` — lookup by id
- `Sequence unique` — enforces deterministic ordering

If the MongoDB user does not have permission to create indexes, initialization may fail. In that case, create the indexes manually and use a user with read/write-only permissions afterwards.

---

## 8) Troubleshooting

### Verify fails immediately
- Check you are using the correct `SecretKey` and `GenesisHash`.
- Ensure you did not reuse a v0.0.2 collection.

### “Another writer holds the lock”
- Only one writer instance should be active per ledger.
- The writer uses a lease in `provance_locks` to prevent forks.

---

## License

MIT
