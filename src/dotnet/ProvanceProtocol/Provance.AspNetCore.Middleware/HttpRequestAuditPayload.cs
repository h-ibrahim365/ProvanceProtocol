namespace Provance.AspNetCore.Middleware
{
    /// <summary>Payload recorded by the middleware for each audited request.</summary>
    public sealed record HttpRequestAuditPayload
    {
        /// <summary>Event type of a request that completed (whatever its status code).</summary>
        public const string CompletedEvent = "HTTP_REQUEST_COMPLETED";

        /// <summary>Event type of a request that ended with an unhandled exception.</summary>
        public const string FailedEvent = "HTTP_REQUEST_FAILED";

        /// <summary>Event type of a request aborted by the client.</summary>
        public const string AbortedEvent = "HTTP_REQUEST_ABORTED";

        /// <summary>Authenticated actor id, or <c>null</c> for anonymous requests.</summary>
        public string? ActorId { get; init; }

        /// <summary>HTTP method.</summary>
        public required string Method { get; init; }

        /// <summary>Request path (PathBase + Path), without the query string.</summary>
        public required string Path { get; init; }

        /// <summary>Query string, only when <see cref="HttpAuditOptions.IncludeQueryString"/> is set.</summary>
        public string? QueryString { get; init; }

        /// <summary>Response status code. 500 when an exception escaped before the response started.</summary>
        public required int StatusCode { get; init; }

        /// <summary><c>completed</c>, <c>failed</c> or <c>aborted</c>.</summary>
        public required string Outcome { get; init; }

        /// <summary>Time spent in the rest of the pipeline, in milliseconds.</summary>
        public required long DurationMs { get; init; }

        /// <summary>Trace identifier of the request, to correlate with application logs.</summary>
        public string? TraceId { get; init; }

        /// <summary>Full type name of the unhandled exception. The message is not recorded, it may contain personal data.</summary>
        public string? ExceptionType { get; init; }

        /// <summary>Client IP, only when <see cref="HttpAuditOptions.IncludeClientIp"/> is set.</summary>
        public string? ClientIp { get; init; }

        /// <summary>User-Agent header, only when <see cref="HttpAuditOptions.IncludeUserAgent"/> is set.</summary>
        public string? UserAgent { get; init; }
    }
}
