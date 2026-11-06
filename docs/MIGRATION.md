# Migrating from 0.0.3 to 0.1.0

0.1.0 introduces ledger format 1. Ledgers written by 0.0.x cannot be verified by 0.1.0 and cannot be
continued. Start a new ledger and keep the old collection as a read-only archive.

## 1. Packages

Update `Provance.Core` and `Provance.AspNetCore.Middleware` to 0.1.0, and replace `Provance.Storage.MongoDB` with `Provance.Storage.EntityFrameworkCore` plus the EF Core provider of your database. `Provance.Storage.MongoDB` is deprecated. `Provance.AspNetCore.Middleware` now references the ASP.NET Core
shared framework instead of the deprecated `Microsoft.AspNetCore.Http.Abstractions` 2.x package.

## 2. Configuration

Before:

```json
"ProvanceProtocol": {
  "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
  "SecretKey": "..."
},
"MongoDb": { "ConnectionString": "...", "DatabaseName": "...", "CollectionName": "ledger_entries_v1" }
```

After:

```json
"Provance": {
  "LedgerId": "orders-api-prod",
  "SigningKeyId": "k1"
},
"ConnectionStrings": { "Audit": "..." }
```

* `LedgerId` is new and required.
* `GenesisHash` is optional; by default it is derived from `LedgerId`.
* `SecretKey` must be at least 32 bytes and must come from a secret store or environment variable
  (`Provance__SecretKey`). Placeholder values are refused.
* `HashAlgorithm` is gone: it was never used.
* Start a **new** ledger (new table or database). The 0.0.3 collection stays as a read-only archive.

## 3. Registration

Before:

```csharp
builder.Services.AddProvanceMongoStorage(builder.Configuration);
builder.Services.AddProvanceLogging(options => { ... });
app.UseProvanceLogger();
```

After:

```csharp
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("Audit")))
    .AddHttpAuditing();

app.UseProvanceAuditing();
```

There is no silent in-memory fallback any more: without `UseEntityFrameworkCore`, `UseInMemoryStore` or `UseStore`,
resolving the ledger fails with an explicit message.

`AddProvance` lives in `Provance.Core`, so worker services and console apps no longer need the
ASP.NET Core package.

## 4. API changes

| 0.0.3 | 0.1.0 |
| :---- | :---- |
| `AddEntryAsync(string, AuditedPayload)` | `AddEntryAsync<TPayload>(string, TPayload)`. Any serializable object; the runtime type is used. |
| `GetLastEntryAsync()` | `GetHeadAsync()` |
| `VerifyChainIntegrityAsync()` returning `(bool, string)` | `VerifyAsync(options)` returning `LedgerVerificationResult` (failure reason, sequence, counts, last hash). |
| `LedgerEntry.CurrentHash` | `LedgerEntry.Hash`, plus `Signature`, `KeyId`, `LedgerId`, `PayloadHash`, `FormatVersion`. |
| `LedgerEntry.Payload` (object) | `LedgerEntry.PayloadJson` (canonical JSON) and `GetPayload<T>()`. |
| `AuditedPayload.CustomData` | `AuditedPayload.Data`. |
| `HttpContextAuditedPayload` | `HttpRequestAuditPayload` (query string, IP and user agent are opt-in). |
| Events `HTTP_REQUEST_SUCCESS / FAILURE / ABORTED` | `HTTP_REQUEST_COMPLETED / FAILED / ABORTED`. |
| `ILedgerStore.WriteEntryAsync`, `GetLastEntryAsync`, `GetAllEntriesAsync`, `AcquireOrRenewLeaseAsync` | `AppendAsync` (batch), `GetHeadAsync`, `ReadAsync` (streaming), `RedactPayloadAsync`. No lease. |
| `LedgerService`, `LedgerWriterService`, `EntryQueue`, `IEntryQueue` public | Internal. Use `ILedgerService`. |
| Polly dependency | Removed. |

New: `CreateCheckpointAsync`, `RedactPayloadAsync`, `LedgerVerifier` (standalone verification),
`ILedgerSigner` (custom or KMS signers), `ILedgerCheckpointSink` and `FileCheckpointSink`.

## 5. Behavior changes

* Several instances of the application can now write to the same ledger. 0.0.3 stopped the host of
  every instance but the first.
* A lost acknowledgement from the store no longer blocks every later append.
* The middleware no longer fails a successful request when the ledger is down (unless
  `FailureMode = Throw`), and waits at most `AuditTimeout`.
* Fields of derived payload types are now signed. In 0.0.3 they were stored but not covered by the hash.

## 6. Staying on MongoDB

Use the official `MongoDB.EntityFrameworkCore` provider with your own context: `AddProvanceLedger()` plus
`ToCollection(...)` in `OnModelCreating`, then `UseEntityFrameworkCore<TContext>(o => o.EnsureSchema = true)`.
The store creates the collection and the unique index on first use. A replica set is recommended. See the
store README. This path is not covered by the test suite yet.

## 7. Custom stores

If you implemented `ILedgerStore`:

* enforce uniqueness of both `Sequence` and `Id` and throw `LedgerConcurrencyException` on conflict;
* insert batches in order;
* store `PayloadJson` byte for byte and timestamps with at least millisecond precision;
* return entries in ascending sequence from `ReadAsync`.
