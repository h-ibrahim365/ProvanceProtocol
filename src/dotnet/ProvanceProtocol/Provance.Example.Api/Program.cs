using Microsoft.EntityFrameworkCore;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;
using Provance.Core.Verification;

var builder = WebApplication.CreateBuilder(args);

// PROVANCE: options come from the "Provance" section. Keep Provance:SecretKey out of appsettings.json
// (user secrets in development, a secret store in production).
// The ledger goes to a local SQLite file so the example runs without installing anything.
// Swap UseSqlite for UseNpgsql, UseSqlServer, UseMySql... in a real application.
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseSqlite(builder.Configuration.GetConnectionString("Audit") ?? "Data Source=provance-example.db"))
    .AddFileCheckpoints(Path.Combine(builder.Environment.ContentRootPath, "checkpoints"))
    .AddHttpAuditing(o => o.IncludeClientIp = false);

var app = builder.Build();

app.UseProvanceAuditing();

// A business endpoint that writes its own, more meaningful audit entry.
app.MapPost("/orders", async (CreateOrder order, ILedgerService ledger, CancellationToken ct) =>
{
    var orderId = Guid.NewGuid();
    await ledger.AddEntryAsync("ORDER_CREATED", new AuditedPayload
    {
        ActorId = order.CustomerId,
        Description = "Order created",
        Data = new Dictionary<string, object?> { ["orderId"] = orderId, ["amount"] = order.Amount }
    }, ct);
    return Results.Created($"/orders/{orderId}", new { orderId });
});

app.MapGet("/ledger/head", async (ILedgerService ledger, CancellationToken ct) =>
    await ledger.GetHeadAsync(ct) is { } head ? Results.Ok(head) : Results.NotFound());

app.MapGet("/ledger/verify", async (ILedgerService ledger, CancellationToken ct) =>
{
    var result = await ledger.VerifyAsync(cancellationToken: ct);
    return result.IsValid ? Results.Ok(result) : Results.Conflict(result);
});

app.MapPost("/ledger/checkpoint", async (ILedgerService ledger, CancellationToken ct) =>
    await ledger.CreateCheckpointAsync(ct) is { } checkpoint ? Results.Ok(checkpoint) : Results.NotFound());

app.MapPost("/ledger/verify-with-checkpoint", async (LedgerCheckpoint checkpoint, ILedgerService ledger, CancellationToken ct) =>
{
    var result = await ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = checkpoint }, ct);
    return result.IsValid ? Results.Ok(result) : Results.Conflict(result);
});

app.Run();

/// <summary>Request body of POST /orders.</summary>
/// <param name="CustomerId">Customer placing the order.</param>
/// <param name="Amount">Amount as a string, to keep decimals exact in the ledger.</param>
internal sealed record CreateOrder(string CustomerId, string Amount);
