using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Provance.AspNetCore.Middleware.Data;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;
using System.Security.Claims;

namespace Provance.AspNetCore.Middleware
{
    /// <summary>
    /// Middleware that intercepts every incoming request, creates an audit entry 
    /// for it, and sends it to the non-blocking ledger service.
    /// </summary>
    public class ProvanceLoggerMiddleware(RequestDelegate next, ILogger<ProvanceLoggerMiddleware> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly ILogger<ProvanceLoggerMiddleware> _logger = logger;

        /// <summary>
        /// Invokes the middleware to process the HTTP request.
        /// </summary>
        /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
        /// <param name="ledgerService">The injected <see cref="ILedgerService"/> used to seal and enqueue the audit entry.</param>
        /// <returns>A <see cref="Task"/> that completes when the request processing is finished.</returns>
        public async Task InvokeAsync(HttpContext context, ILedgerService ledgerService)
        {
            try
            {
                // 1. Process the rest of the pipeline first
                await _next(context);

                // 2. After the request has been processed, log the successful outcome
                // Note: Logging after execution ensures we capture the final state/status code.
                await LogRequestAsync(context, ledgerService, "HTTP_REQUEST_SUCCESS");
            }
            catch (Exception ex)
            {
                // 3. Log the failure before re-throwing the exception
                _logger.LogError(ex, "An unhandled exception occurred during request processing.");
                await LogRequestAsync(context, ledgerService, "HTTP_REQUEST_FAILURE", ex.Message);
                throw;
            }
        }

        private static async Task LogRequestAsync(HttpContext context, ILedgerService ledgerService, string eventType, string? failureReason = null)
        {
            // Get user ID from claims if authenticated
            string? userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                             context.User?.FindFirst("sub")?.Value ?? // Common JWT sub claim
                             context.User?.Claims.FirstOrDefault()?.Value; // Fallback

            var payload = new HttpContextAuditedPayload
            {
                ActorId = userId ?? "ANONYMOUS",
                ActorRole = userId != null ? "AUTHENTICATED_USER" : "GUEST",
                Description = $"Request processed with status code {context.Response.StatusCode}.",
                RequestPath = context.Request.Path + context.Request.QueryString,
                HttpMethod = context.Request.Method,
                UserAgent = context.Request.Headers["User-Agent"].ToString(),
                ClientIpAddress = context.Connection.RemoteIpAddress?.ToString(),
                CustomData = new Dictionary<string, object>
                {
                    { "ResponseStatusCode", context.Response.StatusCode },
                    { "FailureReason", failureReason ?? "N/A" }
                }
            };

            var entry = new LedgerEntry
            {
                EventType = eventType,
                // The correct hash will be fetched and overwritten by SealEntryAsync.
                PreviousHash = "",
                Payload = payload
            };

            // Seal the entry and push it to the background queue (non-blocking)
            // The service is highly concurrent, so we don't await the DB write here.
            await ledgerService.SealEntryAsync(entry);
        }
    }
}