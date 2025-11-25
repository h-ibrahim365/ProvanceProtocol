using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Provance.AspNetCore.Middleware.Data;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;
using System.Security.Claims;

namespace Provance.AspNetCore.Middleware
{
    /// <summary>
    /// Middleware that intercepts incoming HTTP requests, creates an audit entry,
    /// and sends it to the PROVANCE ledger service for tamper-evident persistence.
    /// </summary>
    public class ProvanceLoggerMiddleware(RequestDelegate next, ILogger<ProvanceLoggerMiddleware> logger)
    {
        private readonly RequestDelegate _next = next;
        private readonly ILogger<ProvanceLoggerMiddleware> _logger = logger;

        /// <summary>
        /// Invokes the middleware to process the HTTP request.
        /// Logs a success/failure (or aborted) audit entry after the request is processed.
        /// </summary>
        /// <remarks>
        /// Important: audit logging should not be canceled by <see cref="HttpContext.RequestAborted"/>,
        /// otherwise a client disconnect could prevent the audit trail from being written.
        /// Only application shutdown should cancel the audit operation.
        /// </remarks>
        /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
        /// <param name="ledgerService">The <see cref="ILedgerService"/> used to seal and enqueue audit entries.</param>
        /// <param name="appLifetime">
        /// The application lifetime used to cancel audit work only when the app is stopping.
        /// </param>
        /// <returns>A <see cref="Task"/> that completes when request processing is finished.</returns>
        public async Task InvokeAsync(HttpContext context, ILedgerService ledgerService, IHostApplicationLifetime appLifetime)
        {
            var shutdownToken = appLifetime.ApplicationStopping;

            try
            {
                await _next(context);

                // If the client disconnected after the pipeline ran, record it explicitly.
                var eventType = context.RequestAborted.IsCancellationRequested
                    ? "HTTP_REQUEST_ABORTED"
                    : "HTTP_REQUEST_SUCCESS";

                await LogRequestAsync(
                    context,
                    ledgerService,
                    eventType,
                    failureReason: null,
                    cancellationToken: shutdownToken);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
            {
                // Client disconnected / request aborted: this is not a server error,
                // but we still record it for audit completeness.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Request aborted (client disconnected). Recording HTTP_REQUEST_ABORTED audit entry.");
                }

                try
                {
                    await LogRequestAsync(
                        context,
                        ledgerService,
                        eventType: "HTTP_REQUEST_ABORTED",
                        failureReason: "Client disconnected / request aborted.",
                        cancellationToken: shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // App is stopping: skip audit logging gracefully.
                }

                throw;
            }
            catch (Exception ex) when (!shutdownToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "An unhandled exception occurred during request processing.");

                try
                {
                    await LogRequestAsync(
                        context,
                        ledgerService,
                        eventType: "HTTP_REQUEST_FAILURE",
                        failureReason: ex.Message,
                        cancellationToken: shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // App is stopping: skip audit logging gracefully.
                }

                throw;
            }
        }

        private static async Task LogRequestAsync(
            HttpContext context,
            ILedgerService ledgerService,
            string eventType,
            string? failureReason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? userId =
                context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                context.User?.FindFirst("sub")?.Value ??
                context.User?.Claims.FirstOrDefault()?.Value;

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
                    { "FailureReason", failureReason ?? "N/A" },
                    { "RequestAborted", context.RequestAborted.IsCancellationRequested }
                }
            };

            var entry = new LedgerEntry
            {
                EventType = eventType,
                PreviousHash = string.Empty, // overwritten by SealEntryAsync
                Payload = payload
            };

            await ledgerService.SealEntryAsync(entry, cancellationToken);
        }
    }
}