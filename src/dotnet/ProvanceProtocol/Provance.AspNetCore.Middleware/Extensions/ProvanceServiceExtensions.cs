using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;

namespace Provance.AspNetCore.Middleware.Extensions
{
    /// <summary>
    /// Provides extension methods for adding PROVANCE Core services to the IServiceCollection.
    /// </summary>
    public static class ProvanceServiceExtensions
    {
        /// <summary>
        /// Registers all PROVANCE Core services, including the Ledger and the background writer.
        /// </summary>
        /// <param name="services">The IServiceCollection to register services in.</param>
        /// <param name="configureOptions">Delegate to configure ProvanceOptions (e.g., GenesisHash).</param>
        /// <returns>The updated IServiceCollection.</returns>
        public static IServiceCollection AddProvanceLogging(this IServiceCollection services, Action<ProvanceOptions> configureOptions)
        {
            // --- 1. Configure Options ---
            services.Configure(configureOptions);

            // --- 2. Register Core Services (from Provance.Core) ---
            services.AddSingleton<IEntryQueue, EntryQueue>();
            services.AddSingleton<ILedgerService, LedgerService>();

            // --- 3. Register Hosted Services ---
            // LedgerWriterService is the IHostedService that consumes the queue and writes to the store.
            services.AddHostedService<LedgerWriterService>();

            // --- 4. FALLBACK STORAGE ---
            services.TryAddSingleton<ILedgerStore, InMemoryLedgerStore>();

            // NOTE: LedgerSealerService (Merkle Tree archival) would be added here later.

            return services;
        }
    }
}