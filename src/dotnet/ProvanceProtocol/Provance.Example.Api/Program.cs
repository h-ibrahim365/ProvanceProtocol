using Microsoft.AspNetCore.Mvc;
using Provance.AspNetCore.Middleware.Data;
using Provance.AspNetCore.Middleware.Dtos;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;

// Create the WebApplicationBuilder
var builder = WebApplication.CreateBuilder(args);

// Add services to the IOC container.

// --- PROVANCE CORE REGISTRATION ---
// 1. Storage implementation (In-Memory for the example)
builder.Services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();
// 2. Queue implementation (for Zero-Blocking principle)
builder.Services.AddSingleton<IEntryQueue, EntryQueue>();

// 3. Configuration of Protocol Options
// This step registers the immutable starting point for the cryptographic chain.
// The ILedgerService will read this option via IOptions<ProvanceOptions>.
builder.Services.Configure<Provance.Core.Options.ProvanceOptions>(options =>
{
    // The Genesis Hash MUST be hardcoded and consistent across all deployments.
    options.GenesisHash = "GENESIS_ROOT_HASH_0000000000000000000000000000000000000000000000000000000000000000";
});

// 4. Core Ledger Service (the main application façade)
// This service handles sealing (hashing) new entries and retrieving data.
builder.Services.AddSingleton<ILedgerService, LedgerService>();

// 5. Background Hosted Service (consumes the queue and writes to the store)
// This ensures that the heavy work (DB I/O) happens asynchronously, preventing API thread blocking.
builder.Services.AddHostedService<LedgerWriterService>();
// ----------------------------------


// Add basic services for Swagger/OpenAPI (optional, but useful for testing APIs)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// HTTPS Redirection (good practice)
app.UseHttpsRedirection();

// API Endpoint Definitions

/// <summary>
/// Adds a new transaction to the ledger.
/// The transaction is sealed (hashed/signed) and added to the queue for background writing.
/// </summary>
app.MapPost("/api/ledger/add", async (
    [FromBody] LedgerRequest request,
    HttpContext httpContext,
    ILedgerService ledgerService) =>
{
    var userId = httpContext.User.Identity?.IsAuthenticated == true
        ? httpContext.User.Identity.Name
        : "ANONYMOUS";

    // 2. Construction du HttpContextAuditedPayload complet
    var httpPayload = new HttpContextAuditedPayload
    {
        Description = request.Description,
        CustomData = request.CustomData,

        RequestPath = httpContext.Request.Path,
        HttpMethod = httpContext.Request.Method,
        UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
        ClientIpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
        AuthenticatedUserId = userId,

        ActorId = userId
    };

    var result = await ledgerService.AddEntryAsync(request.EventType, httpPayload);

    // Returns the newly sealed entry with a 201 Created status.
    return Results.Created($"/api/ledger/entry/{result.Id}", result);
})
.WithName("AddLedgerEntry")
.WithSummary("Adds a new structured transaction to the ledger chain (asynchronously).");

/// <summary>
/// Retrieves the latest entry written to the ledger.
/// </summary>
app.MapGet("/api/ledger/last", async (ILedgerService ledgerService) =>
{
    var lastEntry = await ledgerService.GetLastEntryAsync();
    return lastEntry is null ? Results.NotFound("The ledger is empty.") : Results.Ok(lastEntry);
})
.WithName("GetLastEntry")
.WithSummary("Retrieves the last entry in the ledger");

/// <summary>
/// Verifies the integrity of the entire Ledger Chain.
/// </summary>
app.MapGet("/api/ledger/verify", async (ILedgerService ledgerService) =>
{
    // The LedgerService now returns a tuple (bool, string)
    var (isValid, reason) = await ledgerService.VerifyChainIntegrityAsync();

    if (isValid)
    {
        return Results.Ok(new { IsValid = true, Message = "Chain integrity successfully verified. All entries are valid." });
    }
    else
    {
        // Use Results.Conflict (409) to signal an integrity failure
        return Results.Conflict(new { IsValid = false, Message = $"Integrity compromised. Reason: {reason}" });
    }
})
.WithName("VerifyChainIntegrity")
.WithSummary("Verifies if all ledger entries are correctly chained and hashed.");


app.Run();

// Internal class definition for the transaction payload
// This simulates the incoming data for the new ledger entry
internal record TransactionPayload(string Data);