using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Provance.Core.Checkpoints;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Services.Internal;
using Provance.Core.Signing;
using Provance.Core.Verification;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder returned by <c>AddProvance</c>, used to choose a store, a signer and checkpoint sinks.</summary>
    public interface IProvanceBuilder
    {
        /// <summary>The service collection being configured.</summary>
        IServiceCollection Services { get; }
    }

    internal sealed class ProvanceBuilder(IServiceCollection services) : IProvanceBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    /// <summary>Registration of PROVANCE services. Works in ASP.NET Core, worker services and console hosts.</summary>
    public static class ProvanceServiceCollectionExtensions
    {
        /// <summary>Registers PROVANCE and binds <see cref="ProvanceOptions"/> from a configuration section.</summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">Usually <c>builder.Configuration.GetSection("Provance")</c>.</param>
        /// <param name="configure">Optional extra configuration applied after binding.</param>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, IConfiguration configuration, Action<ProvanceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            services.Configure<ProvanceOptions>(configuration);
            if (configure is not null)
            {
                services.PostConfigure(configure);
            }
            return AddProvanceCore(services);
        }

        /// <summary>Registers PROVANCE with options configured in code.</summary>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, Action<ProvanceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            services.Configure(configure);
            return AddProvanceCore(services);
        }

        /// <summary>Uses the in-memory store (tests and demos only, data is lost on restart).</summary>
        public static IProvanceBuilder UseInMemoryStore(this IProvanceBuilder builder)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<ILedgerStore, InMemoryLedgerStore>());
            return builder;
        }

        /// <summary>Uses a specific store instance.</summary>
        public static IProvanceBuilder UseStore(this IProvanceBuilder builder, ILedgerStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            builder.Services.Replace(ServiceDescriptor.Singleton(store));
            return builder;
        }

        /// <summary>Replaces the default HMAC signer, for example with a KMS backed implementation.</summary>
        public static IProvanceBuilder UseSigner<TSigner>(this IProvanceBuilder builder)
            where TSigner : class, ILedgerSigner
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<ILedgerSigner, TSigner>());
            return builder;
        }

        /// <summary>Adds a checkpoint sink. Checkpoints are published every <see cref="ProvanceOptions.CheckpointInterval"/>.</summary>
        public static IProvanceBuilder AddCheckpointSink(this IProvanceBuilder builder, ILedgerCheckpointSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            builder.Services.AddSingleton(sink);
            return builder;
        }

        /// <summary>Adds a <see cref="FileCheckpointSink"/> writing under <paramref name="directory"/>.</summary>
        public static IProvanceBuilder AddFileCheckpoints(this IProvanceBuilder builder, string directory)
            => builder.AddCheckpointSink(new FileCheckpointSink(directory));

        private static ProvanceBuilder AddProvanceCore(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddOptions<ProvanceOptions>().ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ProvanceOptions>, ProvanceOptionsValidator>());

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ILedgerSigner, HmacLedgerSigner>();
            services.TryAddSingleton<EntryQueue>();
            services.TryAddSingleton<LedgerVerifier>();
            services.TryAddSingleton<ILedgerService, LedgerService>();

            // No silent fallback: resolving the store without choosing one fails with an explicit message.
            services.TryAddSingleton<ILedgerStore>(_ => throw new InvalidOperationException(
                "No PROVANCE store is configured. Call UseEntityFrameworkCore(...), UseInMemoryStore() or UseStore(...) after AddProvance(...)."));

            if (!services.Any(d => d.ServiceType == typeof(ProvanceMarker)))
            {
                services.AddSingleton<ProvanceMarker>();
                services.AddHostedService<LedgerWriter>();
                services.AddHostedService<CheckpointPublisher>();
            }

            return new ProvanceBuilder(services);
        }

        private sealed class ProvanceMarker;
    }
}
