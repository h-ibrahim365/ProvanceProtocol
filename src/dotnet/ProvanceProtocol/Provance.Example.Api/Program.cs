using Microsoft.AspNetCore.Mvc;
using Provance.AspNetCore.Middleware.Data;
using Provance.AspNetCore.Middleware.Dtos;
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Core.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// --- PROVANCE SETUP ---------------------------------------------------------

// 1. OPTIONAL: Configure MongoDB Storage
// builder.Services.AddProvanceMongoStorage(builder.Configuration);

// 2. Register Provance Core Services (All-in-One)
builder.Services.AddProvanceLogging(options =>
{
    var provanceConfig = builder.Configuration.GetSection("ProvanceProtocol");

    options.GenesisHash = provanceConfig["GenesisHash"] ?? string.Empty;
    options.SecretKey = provanceConfig["SecretKey"] ?? string.Empty;
});

// ----------------------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// --- API ENDPOINTS ---

app.MapPost("/api/ledger/add", async (
    [FromBody] LedgerRequest request,
    HttpContext httpContext,
    ILedgerService ledgerService,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.Identity?.IsAuthenticated == true
        ? (httpContext.User.Identity?.Name ?? "UNKNOWN")
        : "ANONYMOUS";

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

    var result = await ledgerService.AddEntryAsync(
        request.EventType,
        httpPayload,
        cancellationToken);

    return Results.Created($"/api/ledger/entry/{result.Id}", result);
})
.WithName("AddLedgerEntry")
.WithSummary("Adds a new structured transaction to the ledger chain (asynchronously).");

app.MapGet("/api/ledger/last", async (
    ILedgerService ledgerService,
    CancellationToken cancellationToken) =>
{
    var lastEntry = await ledgerService.GetLastEntryAsync(cancellationToken);

    return lastEntry is null
        ? Results.NotFound("The ledger is empty.")
        : Results.Ok(lastEntry);
})
.WithName("GetLastEntry")
.WithSummary("Retrieves the last entry in the ledger");

app.MapGet("/api/ledger/verify", async (
    ILedgerService ledgerService,
    CancellationToken cancellationToken) =>
{
    var (isValid, reason) = await ledgerService.VerifyChainIntegrityAsync(cancellationToken);

    return isValid
        ? Results.Ok(new { IsValid = true, Message = "Chain integrity successfully verified. All entries are valid." })
        : Results.Conflict(new { IsValid = false, Message = $"Integrity compromised. Reason: {reason}" });
})
.WithName("VerifyChainIntegrity")
.WithSummary("Verifies if all ledger entries are correctly chained and signed (HMAC).");

app.Run();