using Provance.Core.Data;

namespace Provance.AspNetCore.Middleware.Data
{
    /// <summary>
    /// Extends the core AuditedPayload to include common HTTP context details.
    /// </summary>
    public class HttpContextAuditedPayload : AuditedPayload
    {
        /// <summary>
        /// The path and query of the incoming request (e.g., /api/users?id=123).
        /// </summary>
        public string? RequestPath { get; set; }

        /// <summary>
        /// The HTTP method (e.g., GET, POST, DELETE).
        /// </summary>
        public string? HttpMethod { get; set; }

        /// <summary>
        /// The user agent string from the request headers.
        /// </summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// The IP address of the client making the request.
        /// </summary>
        public string? ClientIpAddress { get; set; }

        /// <summary>
        /// The user identifier extracted from the Authentication context (e.g., JWT Claim).
        /// </summary>
        public string? AuthenticatedUserId { get; set; }

        // Inherits Description and CustomData from AuditedPayload
    }
}