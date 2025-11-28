<!-- Provance.Core README (v0.0.3) -->
<p align="center">
  <img src="provanceCore.png" alt="PROVANCE Core logo" width="120" />
</p>

# Provance.Core

**Core SDK for PROVANCE**: tamper-evident audit trails via **HMAC-SHA256 hash-chaining** and a **single-writer ledger** (anti-fork) model.

- Repo docs: see the root README in the repository.
- NuGet: `Provance.Core`

## Install

```bash
dotnet add package Provance.Core
```

## What you get

- `LedgerEntry` model (hash-chaining fields + `Sequence`)
- `ILedgerService` for producing entries and verifying integrity
- Single Writer background service (linearizes concurrent producers)
- In-memory store for demos/tests (`InMemoryLedgerStore`)

> For ASP.NET Core integration, use **Provance.AspNetCore.Middleware**.

## Minimal DI (Worker / Console)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;

var host = Host.CreateDefaultBuilder(args)
  .ConfigureServices(services =>
  {
      services.Configure<ProvanceOptions>(o =>
      {
          o.GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
          o.SecretKey = "CHANGE_ME_IN_PROD";
      });

      services.AddSingleton<IEntryQueue, EntryQueue>();
      services.AddSingleton<ILedgerService, LedgerService>();

      // DEV/TEST ONLY (use a real store in prod)
      services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();

      // Single Writer
      services.AddHostedService<LedgerWriterService>();
  })
  .Build();

await host.RunAsync();
```

## Compatibility notes

- v0.x is pre-release: breaking changes may occur.
- v0.0.3 introduces `Sequence` as the deterministic ordering key; start a new ledger collection when upgrading from v0.0.2.

## License

MIT
