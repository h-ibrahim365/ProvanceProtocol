# Provance.AspNetCore.Middleware

Records one PROVANCE ledger entry per HTTP request: method, path, status code, outcome, duration,
trace id, actor id and exception type.

```bash
dotnet add package Provance.AspNetCore.Middleware
```

```csharp
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(builder.Configuration.GetConnectionString("Audit")))
    .AddHttpAuditing(o =>
    {
        o.FailureMode = HttpAuditFailureMode.LogAndContinue;  // or Throw
        o.IncludeClientIp = false;                            // personal data, off by default
    });

app.UseAuthentication();
app.UseProvanceAuditing();
```

Query strings, IP addresses and user agents are off by default; exception messages are never recorded.
`/health`, `/healthz`, `/metrics` and `/favicon.ico` are excluded. The wait for the ledger is bounded by
`AuditTimeout` (5 s).

The entry is written after the endpoint ran. For operations that must not happen without an audit trace,
call `ILedgerService.AddEntryAsync` in the endpoint before doing the work.

Documentation: https://github.com/h-ibrahim365/ProvanceProtocol
