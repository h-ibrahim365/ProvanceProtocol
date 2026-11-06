using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Provance.Core.Services.Interfaces;

namespace Provance.AspNetCore.Middleware
{
    /// <summary>
    /// Records one ledger entry per request, after the rest of the pipeline has run.
    /// </summary>
    internal sealed class ProvanceAuditMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly HttpAuditOptions _options;
        private readonly ILogger<ProvanceAuditMiddleware> _logger;

        public ProvanceAuditMiddleware(RequestDelegate next, IOptions<HttpAuditOptions> options, ILogger<ProvanceAuditMiddleware> logger)
        {
            _next = next;
            _options = options.Value;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, ILedgerService ledger, IHostApplicationLifetime lifetime)
        {
            if (!_options.IsAudited(context))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The original exception always wins: an audit failure here is logged, never rethrown.
                await AuditAsync(context, ledger, lifetime, ex, start, allowThrow: false).ConfigureAwait(false);
                throw;
            }

            await AuditAsync(context, ledger, lifetime, null, start, allowThrow: _options.FailureMode == HttpAuditFailureMode.Throw).ConfigureAwait(false);
        }

        private async Task AuditAsync(HttpContext context, ILedgerService ledger, IHostApplicationLifetime lifetime, Exception? exception, long start, bool allowThrow)
        {
            bool aborted = context.RequestAborted.IsCancellationRequested;
            string eventType;
            string outcome;
            if (aborted && (exception is null || exception is OperationCanceledException))
            {
                eventType = HttpRequestAuditPayload.AbortedEvent;
                outcome = "aborted";
            }
            else if (exception is not null)
            {
                eventType = HttpRequestAuditPayload.FailedEvent;
                outcome = "failed";
            }
            else
            {
                eventType = HttpRequestAuditPayload.CompletedEvent;
                outcome = "completed";
            }

            var request = context.Request;
            int statusCode = exception is not null && !context.Response.HasStarted
                ? StatusCodes.Status500InternalServerError
                : context.Response.StatusCode;

            var payload = new HttpRequestAuditPayload
            {
                ActorId = _options.ActorIdResolver(context),
                Method = request.Method,
                Path = (request.PathBase + request.Path).ToString(),
                QueryString = _options.IncludeQueryString && request.QueryString.HasValue ? request.QueryString.Value : null,
                StatusCode = statusCode,
                Outcome = outcome,
                DurationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ExceptionType = exception?.GetType().FullName,
                ClientIp = _options.IncludeClientIp ? context.Connection.RemoteIpAddress?.ToString() : null,
                UserAgent = _options.IncludeUserAgent && request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null
            };

            // Not linked to RequestAborted: a client disconnect must not prevent the audit entry.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(_options.AuditTimeout);

            try
            {
                await ledger.AddEntryAsync(eventType, payload, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PROVANCE could not record the audit entry for {Method} {Path}.", payload.Method, payload.Path);
                if (allowThrow)
                {
                    throw;
                }
            }
        }
    }
}
