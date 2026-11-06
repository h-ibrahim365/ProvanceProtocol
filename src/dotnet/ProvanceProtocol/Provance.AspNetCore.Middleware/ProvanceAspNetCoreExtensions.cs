using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Provance.AspNetCore.Middleware;

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>Pipeline registration of the PROVANCE HTTP audit middleware.</summary>
    public static class ProvanceApplicationBuilderExtensions
    {
        /// <summary>
        /// Records one ledger entry per request. Place it after authentication so the actor is known,
        /// and before the endpoints.
        /// </summary>
        public static IApplicationBuilder UseProvanceAuditing(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<ProvanceAuditMiddleware>();
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Service registration of the PROVANCE HTTP audit middleware.</summary>
    public static class ProvanceHttpAuditingBuilderExtensions
    {
        /// <summary>Configures the HTTP audit middleware. Optional: defaults apply when not called.</summary>
        public static IProvanceBuilder AddHttpAuditing(this IProvanceBuilder builder, Action<HttpAuditOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var options = builder.Services.AddOptions<HttpAuditOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }
            return builder;
        }
    }
}
