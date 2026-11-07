# PROVANCE for .NET

.NET 8 and later. For what PROVANCE does and what it detects, see the [main README](../../README.md), or try the
[live demo](https://provance.iamhakim.com) in your browser.

| Package | Use |
| :------ | :-- |
| [`Provance.Core`](https://www.nuget.org/packages/Provance.Core) | Ledger, verification, checkpoints. Works in any .NET 8+ host. |
| [`Provance.Storage.EntityFrameworkCore`](https://www.nuget.org/packages/Provance.Storage.EntityFrameworkCore) | Store for any database with an EF Core provider. |
| [`Provance.AspNetCore.Middleware`](https://www.nuget.org/packages/Provance.AspNetCore.Middleware) | One audit entry per HTTP request. |

## Setting it up

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
| MongoDB | `MongoDB.EntityFrameworkCore` provider, with your own context | No, see the [store README](ProvanceProtocol/Provance.Storage.EntityFrameworkCore/README.md) |
| Anything else | implement `ILedgerStore` (5 methods) | Contract in [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md) |

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

## Several ledgers

One ledger per tenant, or one for audit and one for billing: register each under a name. Each has its own options,
store, keys, archive and checkpoints, and its own chain.

```csharp
builder.Services.AddProvance("billing", builder.Configuration.GetSection("Provance:Ledgers:billing"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(connectionString));        // table provance_ledger_billing
builder.Services.AddProvance("access", builder.Configuration.GetSection("Provance:Ledgers:access"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(connectionString))         // table provance_ledger_access
    .AddHttpAuditing();                                                   // the HTTP middleware writes here

app.MapPost("/invoices", async ([FromKeyedServices("billing")] ILedgerService billing) => { /* ... */ });
```

The default ledger (`AddProvance(section)`) can coexist with named ones. Give every ledger its own `LedgerId`.

## Archive copy and retention

```csharp
builder.Services.AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(appConnectionString))
    .UseEntityFrameworkCoreArchive(db => db.UseNpgsql(archiveConnectionString));

var comparison = await ledger.CompareWithArchiveAsync();   // which field changed, and its original value
```

Every entry is copied to the archive in the background. When verification fails, the comparison shows what the
entry said before. With `Provance:Retention:KeepFor` set, entries older than that leave the primary store once the
archive holds them, and the ledger still verifies. See [ARCHIVE.md](../../docs/ARCHIVE.md) and
[RETENTION.md](../../docs/RETENTION.md).

`LedgerJsonLines` exports a ledger to the JSON Lines format shared by the three libraries, and reads such an export
back to verify or compare it.

## More

* [Implementation notes](../../docs/ARCHITECTURE.md): writer, concurrency, store contract
* [Migration from 0.0.3](../../docs/MIGRATION.md)
