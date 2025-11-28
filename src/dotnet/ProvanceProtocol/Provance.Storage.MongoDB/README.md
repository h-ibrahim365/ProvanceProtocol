<!-- Provance.Storage.MongoDB README (v0.0.3) -->
<p align="center">
  <img src="provanceMongoDb.png" alt="PROVANCE MongoDB logo" width="120" />
</p>

# Provance.Storage.MongoDB

MongoDB storage provider for PROVANCE (`ILedgerStore`), including a simple **single-writer lease** mechanism.

- NuGet: `Provance.Storage.MongoDB`

## Install

```bash
dotnet add package Provance.Storage.MongoDB
```

## Configure

```json
{
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries_v1"
  }
}
```

## Register

```csharp
using Provance.Storage.MongoDB.Extensions;

builder.Services.AddProvanceMongoStorage(builder.Configuration);
```

## What it creates

- Ledger entries collection (your configured `CollectionName`)
- Lock/lease collection: `provance_locks`

Recommended indexes (v0.0.3+ new ledger collection):
- chain head lookup by `Sequence` (descending)
- lookup by `Id`
- **unique** `Sequence` (prevents duplicates and enforces deterministic ordering)

The MongoDB store creates these indexes automatically on startup.
If your Mongo user cannot create indexes, initialization will fail. In that case, create the indexes manually.

## License

MIT
