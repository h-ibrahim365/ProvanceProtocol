using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Provance.Core.Archive;
using Provance.Core.Checkpoints;
using Provance.Core.Comparison;
using Provance.Core.Options;
using Provance.Core.Retention;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;
using Provance.Core.Verification;

namespace Provance.Core.Services.Internal
{
    /// <summary>
    /// Everything one ledger needs, built once per ledger name: options, store, signer, queue, writer, checkpoint
    /// publisher, archive copy and retention. The default ledger has an empty name.
    /// </summary>
    internal sealed class LedgerRuntime
    {
        private LedgerRuntime(string name, ProvanceOptions options, ILedgerStore store, ILedgerStore? archive, ILedgerSigner signer,
            IEnumerable<ILedgerCheckpointSink> sinks, TimeProvider time, ILoggerFactory loggers)
        {
            Name = name;
            Options = options;
            Store = store;
            Signer = signer;
            Time = time;
            Queue = new EntryQueue(options);
            Verifier = new LedgerVerifier(store, signer, options.LedgerId, options.EffectiveGenesisHash);
            Comparer = new LedgerComparer(signer, options.LedgerId);
            Copier = new ArchiveCopier(store, archive, Comparer, options, time, loggers.CreateLogger<ArchiveCopier>());
            Writer = new LedgerWriter(Queue, store, signer, options, time, loggers.CreateLogger<LedgerWriter>(), Copier.Signal);
            Service = new LedgerService(this, loggers.CreateLogger<LedgerService>());
            Publisher = new CheckpointPublisher(Service, sinks, options, time, loggers.CreateLogger<CheckpointPublisher>());
            Retention = new RetentionService(Service, options, time, loggers.CreateLogger<RetentionService>());
        }

        public string Name { get; }

        public ProvanceOptions Options { get; }

        public ILedgerStore Store { get; }

        public ILedgerSigner Signer { get; }

        public TimeProvider Time { get; }

        public EntryQueue Queue { get; }

        public LedgerVerifier Verifier { get; }

        public LedgerComparer Comparer { get; }

        public ArchiveCopier Copier { get; }

        public LedgerWriter Writer { get; }

        public LedgerService Service { get; }

        public CheckpointPublisher Publisher { get; }

        public RetentionService Retention { get; }

        public static LedgerRuntime Create(IServiceProvider services, string name)
        {
            var options = services.GetRequiredService<IOptionsMonitor<ProvanceOptions>>().Get(name);
            bool isDefault = name.Length == 0;

            ILedgerStore store = isDefault
                ? services.GetRequiredService<ILedgerStore>()
                : services.GetKeyedService<ILedgerStore>(name) ?? throw new InvalidOperationException(
                    $"No PROVANCE store is configured for ledger '{name}'. Call UseEntityFrameworkCore(...), UseInMemoryStore() or UseStore(...) on the builder returned by AddProvance(\"{name}\", ...).");

            ILedgerSigner signer = isDefault
                ? services.GetRequiredService<ILedgerSigner>()
                : services.GetKeyedService<ILedgerSigner>(name) ?? new HmacLedgerSigner(options.SigningKeyId, options.SecretKey, options.RetiredKeys);

            var sinks = isDefault
                ? services.GetServices<ILedgerCheckpointSink>()
                : services.GetKeyedServices<ILedgerCheckpointSink>(name);

            var archive = services.GetKeyedService<ILedgerStore>(ProvanceServiceKeys.Archive(name));
            if (archive is not null && ReferenceEquals(archive, store))
            {
                throw new InvalidOperationException($"The archive of ledger '{options.LedgerId}' is the same store as its primary store.");
            }

            return new LedgerRuntime(name, options, store, archive, signer, sinks,
                services.GetService<TimeProvider>() ?? TimeProvider.System,
                services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance);
        }
    }

    /// <summary>Keys of the keyed services that belong to a ledger.</summary>
    internal static class ProvanceServiceKeys
    {
        public static string Archive(string ledgerName) => "provance-archive:" + ledgerName;
    }
}
