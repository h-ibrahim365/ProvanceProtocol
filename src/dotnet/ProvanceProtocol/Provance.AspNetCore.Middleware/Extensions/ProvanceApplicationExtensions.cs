using Microsoft.AspNetCore.Builder;

namespace Provance.AspNetCore.Middleware.Extensions
{
    /// <summary>
    /// Provides extension method for using the PROVANCE Logger Middleware.
    /// </summary>
    public static class ProvanceApplicationExtensions
    {
        /// <summary>
        /// Adds the Provance Logger Middleware to the application's request pipeline.
        /// It should be placed early in the pipeline to capture all requests.
        /// </summary>
        /// <param name="app">The IApplicationBuilder instance.</param>
        /// <returns>The updated IApplicationBuilder.</returns>
        public static IApplicationBuilder UseProvanceLogger(this IApplicationBuilder app)
        {
            return app.UseMiddleware<ProvanceLoggerMiddleware>();
        }
    }
}