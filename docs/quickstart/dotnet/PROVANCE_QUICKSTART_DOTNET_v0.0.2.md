# 🚀 PROVANCE Quick Start & Integration Guide (.NET)

**Doc Version:** 0.0.2  
**Applies to:** Provance.Core + Provance.AspNetCore.Middleware (+ optional Provance.Storage.MongoDB)

---

## 1) Install packages

```bash
dotnet add package Provance.Core
dotnet add package Provance.AspNetCore.Middleware
# Optional (recommended for production persistence)
dotnet add package Provance.Storage.MongoDB
```

---

## 2) Configure (appsettings.json)

```json
{
  "ProvanceProtocol": {
    "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
    "SecretKey": "CHANGE_ME_IN_PROD"
  },
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries"
  }
}
```

- `GenesisHash` must never change after deployment.
- `SecretKey` is used for **HMAC-SHA256** signing (keep it secret; prefer env vars / vault in prod).

---

## 3) Register services (Program.cs)

```csharp
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Storage.MongoDB.Extensions;

var builder = WebApplication.CreateBuilder(args);

// OPTIONAL: Uncomment to enable MongoDB persistence.
// If omitted, Provance will fallback to an in-memory store for instant dev testing.
// builder.Services.AddProvanceMongoStorage(builder.Configuration);

builder.Services.AddProvanceLogging(options =>
{
    var cfg = builder.Configuration.GetSection("ProvanceProtocol");
    options.GenesisHash = cfg["GenesisHash"] ?? string.Empty;
    options.SecretKey   = cfg["SecretKey"]   ?? string.Empty;
});

var app = builder.Build();

// Add middleware early in the pipeline
app.UseProvanceLogger();

app.MapControllers();
app.Run();
```

---

## 4) Verify the ledger

Use `ILedgerService.VerifyChainIntegrityAsync()` to confirm:
- chain linkage is intact
- signatures match stored hashes

---

## 5) Performance tips

- Keep payload size reasonable (payload influences hash cost and storage).
- If you expect long store outages, consider configuring:
  - queue capacity
  - backpressure strategy
  - (future) durable outbox fallback for strict non-blocking.
