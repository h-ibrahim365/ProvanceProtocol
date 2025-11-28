<!-- Provance.AspNetCore.Middleware README (v0.0.3) -->
<p align="center">
  <img src="provanceMiddleWare.png" alt="PROVANCE Middleware logo" width="120" />
</p>

# Provance.AspNetCore.Middleware

ASP.NET Core middleware + DI helpers for integrating PROVANCE tamper-evident audit trails into the request pipeline.

- NuGet: `Provance.AspNetCore.Middleware`

## Install

```bash
dotnet add package Provance.AspNetCore.Middleware
# recommended storage
dotnet add package Provance.Storage.MongoDB
```

## Setup (Minimal API)

```csharp
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Storage.MongoDB.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProvanceMongoStorage(builder.Configuration);

builder.Services.AddProvanceLogging(options =>
{
    var cfg = builder.Configuration.GetSection("ProvanceProtocol");
    options.GenesisHash = cfg["GenesisHash"] ?? string.Empty;
    options.SecretKey = cfg["SecretKey"] ?? string.Empty;
});

var app = builder.Build();

app.UseProvanceLogger();

app.Run();
```

## Notes

- v0.0.3 acknowledges requests only after the Single Writer has persisted the entry (strong correctness, higher latency).
- v0.0.4 will introduce durable outbox + overload policies.

## License

MIT
