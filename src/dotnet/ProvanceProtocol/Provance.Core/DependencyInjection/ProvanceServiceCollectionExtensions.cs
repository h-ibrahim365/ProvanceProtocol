using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    /// <summary>Builder returned by <c>AddProvance</c>, used to choose a store, a signer, an archive and checkpoint sinks.</summary>
    public interface IProvanceBuilder
    {
        /// <summary>The service collection being configured.</summary>
        IServiceCollection Services { get; }

        /// <summary>Name of the ledger this builder configures: empty for the default ledger.</summary>
        string LedgerName => string.Empty;
    }

    internal sealed class ProvanceBuilder(IServiceCollection services, string ledgerName) : IProvanceBuilder
    {
        public IServiceCollection Services { get; } = services;

        public string LedgerName { get; } = ledgerName;
    }

    /// <summary>Registration of PROVANCE services. Works in ASP.NET Core, worker services and console hosts.</summary>
    public static class ProvanceServiceCollectionExtensions
    {
        /// <summary>Registers the default ledger and binds <see cref="ProvanceOptions"/> from a configuration section.</summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">Usually <c>builder.Configuration.GetSection("Provance")</c>.</param>
        /// <param name="configure">Optional extra configuration applied after binding.</param>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, IConfiguration configuration, Action<ProvanceOptions>? configure = null)
            => AddProvance(services, string.Empty, configuration, configure);

        /// <summary>Registers the default ledger with options configured in code.</summary>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, Action<ProvanceOptions> configure)
            => AddProvance(services, string.Empty, configure);

        /// <summary>
        /// Registers a named ledger, bound from a configuration section. Inject it with
        /// <c>[FromKeyedServices("name")] ILedgerService</c>. Each ledger has its own options, store, keys, archive and checkpoints.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="name">Name of the ledger in this application, for example <c>billing</c>. Not stored in the ledger.</param>
        /// <param name="configuration">For example <c>builder.Configuration.GetSection("Provance:Ledgers:billing")</c>.</param>
        /// <param name="configure">Optional extra configuration applied after binding.</param>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, string name, IConfiguration configuration, Action<ProvanceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(configuration);
            services.Configure<ProvanceOptions>(name, configuration);
            if (configure is not null)
            {
                services.PostConfigure(name, configure);
            }
            return AddLedger(services, name);
        }

        /// <summary>Registers a named ledger with options configured in code. Inject it with <c>[FromKeyedServices("name")] ILedgerService</c>.</summary>
        public static IProvanceBuilder AddProvance(this IServiceCollection services, string name, Action<ProvanceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(configure);
            services.Configure(name, configure);
            return AddLedger(services, name);
        }

        /// <summary>Uses the in-memory store (tests and demos only, data is lost on restart).</summary>
        public static IProvanceBuilder UseInMemoryStore(this IProvanceBuilder builder)
            => builder.UseStore(new InMemoryLedgerStore());

        /// <summary>Uses a specific store instance.</summary>
        public static IProvanceBuilder UseStore(this IProvanceBuilder builder, ILedgerStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            return builder.UseStore(_ => store);
        }

        /// <summary>Uses a store created from the service provider (called once, when the ledger is first used).</summary>
        public static IProvanceBuilder UseStore(this IProvanceBuilder builder, Func<IServiceProvider, ILedgerStore> factory)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);
            string name = builder.LedgerName;
            if (name.Length == 0)
            {
                builder.Services.Replace(ServiceDescriptor.Singleton(factory));
            }
            else
            {
                ReplaceKeyed(builder.Services, typeof(ILedgerStore), name, (sp, _) => factory(sp));
            }
            return builder;
        }

        /// <summary>
        /// Copies every entry to a second store, the archive, in the background. Give the application an account that can
        /// only insert into it (and update the payload column, for erasure requests). See docs/ARCHIVE.md.
        /// </summary>
        public static IProvanceBuilder UseArchive(this IProvanceBuilder builder, ILedgerStore archive)
        {
            ArgumentNullException.ThrowIfNull(archive);
            return builder.UseArchive(_ => archive);
        }

        /// <summary>Copies every entry to an archive created from the service provider.</summary>
        public static IProvanceBuilder UseArchive(this IProvanceBuilder builder, Func<IServiceProvider, ILedgerStore> factory)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);
            ReplaceKeyed(builder.Services, typeof(ILedgerStore), ProvanceServiceKeys.Archive(builder.LedgerName), (sp, _) => factory(sp));
            return builder;
        }

        /// <summary>Replaces the default HMAC signer, for example with a KMS backed implementation.</summary>
        public static IProvanceBuilder UseSigner<TSigner>(this IProvanceBuilder builder)
            where TSigner : class, ILedgerSigner
        {
            ArgumentNullException.ThrowIfNull(builder);
            string name = builder.LedgerName;
            if (name.Length == 0)
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<ILedgerSigner, TSigner>());
            }
            else
            {
                RemoveKeyed(builder.Services, typeof(ILedgerSigner), name);
                builder.Services.AddKeyedSingleton<ILedgerSigner, TSigner>(name);
            }
            return builder;
        }

        /// <summary>Adds a checkpoint sink. Checkpoints are published every <see cref="ProvanceOptions.CheckpointInterval"/>.</summary>
        public static IProvanceBuilder AddCheckpointSink(this IProvanceBuilder builder, ILedgerCheckpointSink sink)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(sink);
            string name = builder.LedgerName;
            if (name.Length == 0)
            {
                builder.Services.AddSingleton(sink);
            }
            else
            {
                builder.Services.AddKeyedSingleton(name, sink);
            }
            return builder;
        }

        /// <summary>Adds a <see cref="FileCheckpointSink"/> writing under <paramref name="directory"/>.</summary>
        public static IProvanceBuilder AddFileCheckpoints(this IProvanceBuilder builder, string directory)
            => builder.AddCheckpointSink(new FileCheckpointSink(directory));

        private static ProvanceBuilder AddLedger(IServiceCollection services, string name)
        {
            services.AddOptions<ProvanceOptions>(name).ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ProvanceOptions>, ProvanceOptionsValidator>());
            services.TryAddSingleton(TimeProvider.System);

            var registry = services.FirstOrDefault(d => d.ServiceType == typeof(LedgerRegistry))?.ImplementationInstance as LedgerRegistry;
            if (registry is null)
            {
                registry = new LedgerRegistry();
                services.AddSingleton(registry);
            }
            if (!registry.Names.Add(name))
            {
                return new ProvanceBuilder(services, name); // AddProvance called again for the same ledger: options accumulate
            }

            services.AddKeyedSingleton(name, (sp, key) => LedgerRuntime.Create(sp, (string)key!));

            if (name.Length == 0)
            {
                services.TryAddSingleton<ILedgerSigner, HmacLedgerSigner>();
                services.TryAddSingleton<ILedgerService>(sp => sp.GetRequiredKeyedService<LedgerRuntime>(string.Empty).Service);
                services.TryAddSingleton(sp => sp.GetRequiredKeyedService<LedgerRuntime>(string.Empty).Verifier);

                // No silent fallback: resolving the store without choosing one fails with an explicit message.
                services.TryAddSingleton<ILedgerStore>(_ => throw new InvalidOperationException(
                    "No PROVANCE store is configured. Call UseEntityFrameworkCore(...), UseInMemoryStore() or UseStore(...) after AddProvance(...)."));
            }
            else
            {
                services.AddKeyedSingleton<ILedgerService>(name, (sp, key) => sp.GetRequiredKeyedService<LedgerRuntime>(key).Service);
                services.AddKeyedSingleton(name, (sp, key) => sp.GetRequiredKeyedService<LedgerRuntime>(key).Verifier);
            }

            // Started in this order and stopped in reverse: the archive copy stops last, after the writer has drained.
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<LedgerRuntime>(name).Copier);
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<LedgerRuntime>(name).Writer);
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<LedgerRuntime>(name).Publisher);
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<LedgerRuntime>(name).Retention);

            return new ProvanceBuilder(services, name);
        }

        private static void ReplaceKeyed(IServiceCollection services, Type serviceType, object key, Func<IServiceProvider, object?, object> factory)
        {
            RemoveKeyed(services, serviceType, key);
            services.Add(new ServiceDescriptor(serviceType, key, factory, ServiceLifetime.Singleton));
        }

        private static void RemoveKeyed(IServiceCollection services, Type serviceType, object key)
        {
            for (int i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == serviceType && services[i].IsKeyedService && Equals(services[i].ServiceKey, key))
                {
                    services.RemoveAt(i);
                }
            }
        }

        /// <summary>Names of the ledgers registered in a service collection.</summary>
        private sealed class LedgerRegistry
        {
            public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        }
    }
}
