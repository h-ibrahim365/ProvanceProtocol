using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Provance.AspNetCore.Middleware
{
    /// <summary>What the middleware does when the audit entry cannot be written.</summary>
    public enum HttpAuditFailureMode
    {
        /// <summary>Log the error and let the response go through. The request is not audited.</summary>
        LogAndContinue,

        /// <summary>
        /// Throw, so the request ends with an error when the response has not started yet.
        /// The audit runs after the endpoint: this does not undo what the endpoint already did.
        /// </summary>
        Throw
    }

    /// <summary>Options of the HTTP audit middleware. Privacy sensitive fields are off by default.</summary>
    public sealed class HttpAuditOptions
    {
        /// <summary>Behavior when the ledger cannot be written. Default <see cref="HttpAuditFailureMode.LogAndContinue"/>.</summary>
        public HttpAuditFailureMode FailureMode { get; set; } = HttpAuditFailureMode.LogAndContinue;

        /// <summary>Maximum time the request waits for its audit entry. Default 5 seconds.</summary>
        public TimeSpan AuditTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Record the query string. Off by default: query strings often carry tokens and personal data.</summary>
        public bool IncludeQueryString { get; set; }

        /// <summary>Record the client IP address. Off by default: an IP address is personal data under GDPR.</summary>
        public bool IncludeClientIp { get; set; }

        /// <summary>Record the User-Agent header. Off by default.</summary>
        public bool IncludeUserAgent { get; set; }

        /// <summary>Path prefixes that are never audited.</summary>
        public IList<PathString> ExcludedPaths { get; } = new List<PathString>
        {
            "/health",
            "/healthz",
            "/metrics",
            "/favicon.ico"
        };

        /// <summary>Optional predicate. When set, a request is audited only if it returns <c>true</c> (after <see cref="ExcludedPaths"/>).</summary>
        public Func<HttpContext, bool>? ShouldAudit { get; set; }

        /// <summary>
        /// Resolves the actor id. Default: the <c>NameIdentifier</c> claim, then <c>sub</c>. Return <c>null</c> for anonymous.
        /// </summary>
        public Func<HttpContext, string?> ActorIdResolver { get; set; } = DefaultActorId;

        internal bool IsAudited(HttpContext context)
        {
            foreach (var prefix in ExcludedPaths)
            {
                if (context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return ShouldAudit?.Invoke(context) ?? true;
        }

        private static string? DefaultActorId(HttpContext context)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }
            return user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        }
    }
}
