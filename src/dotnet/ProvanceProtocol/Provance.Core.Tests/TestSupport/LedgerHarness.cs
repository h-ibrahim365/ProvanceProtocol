using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;

namespace Provance.Core.Tests.TestSupport
{
    /// <summary>One "application instance": a service provider with PROVANCE registered and its hosted services started.</summary>
    internal sealed class LedgerHarness : IAsyncDisposable
    {
        public const string LedgerId = "test-ledger";
        public const string Secret = "unit-test-secret-key-0123456789abcdef";

        private readonly List<IHostedService> _hosted;
        private bool _stopped;

        private LedgerHarness(ServiceProvider services, List<IHostedService> hosted)
        {
            Services = services;
            _hosted = hosted;
            Ledger = services.GetRequiredService<ILedgerService>();
            Store = services.GetRequiredService<ILedgerStore>();
        }

        public ServiceProvider Services { get; }

        public ILedgerService Ledger { get; }

        public ILedgerStore Store { get; }

        public static async Task<LedgerHarness> StartAsync(
            ILedgerStore? store = null,
            Action<ProvanceOptions>? configure = null,
            Action<IProvanceBuilder>? build = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var builder = services.AddProvance(o =>
            {
                o.LedgerId = LedgerId;
                o.SecretKey = Secret;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                configure?.Invoke(o);
            });
            builder.UseStore(store ?? new InMemoryLedgerStore());
            build?.Invoke(builder);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }
            return new LedgerHarness(provider, hosted);
        }

        public async Task StopAsync(TimeSpan? timeout = null)
        {
            if (_stopped) return;
            _stopped = true;
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            for (int i = _hosted.Count - 1; i >= 0; i--)
            {
                await _hosted[i].StopAsync(cts.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Services.DisposeAsync();
        }
    }
}
