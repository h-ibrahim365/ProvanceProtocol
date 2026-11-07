using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Tests.TestSupport;

namespace Provance.Core.Tests
{
    /// <summary>Several ledgers in one application, registered with <c>AddProvance(name, ...)</c>.</summary>
    public class NamedLedgerTests
    {
        private static async Task<(ServiceProvider Provider, List<IHostedService> Hosted)> StartAsync(Action<IServiceCollection> register)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            register(services);
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }
            return (provider, hosted);
        }

        private static async Task StopAsync(ServiceProvider provider, List<IHostedService> hosted)
        {
            for (int i = hosted.Count - 1; i >= 0; i--)
            {
                await hosted[i].StopAsync(CancellationToken.None);
            }
            await provider.DisposeAsync();
        }

        [Fact]
        public async Task Each_named_ledger_has_its_own_chain_store_and_key()
        {
            var defaultStore = new InMemoryLedgerStore();
            var billingStore = new InMemoryLedgerStore();
            var accessStore = new InMemoryLedgerStore();
            var (provider, hosted) = await StartAsync(services =>
            {
                services.AddProvance(o => { o.LedgerId = "app"; o.SecretKey = LedgerHarness.Secret; }).UseStore(defaultStore);
                services.AddProvance("billing", o => { o.LedgerId = "billing-prod"; o.SecretKey = "billing-secret-key-0123456789abcdef0123"; }).UseStore(billingStore);
                services.AddProvance("access", o => { o.LedgerId = "access-prod"; o.SecretKey = "access-secret-key-0123456789abcdef01234"; o.SigningKeyId = "a1"; }).UseStore(accessStore);
            });

            var app = provider.GetRequiredService<ILedgerService>();
            var billing = provider.GetRequiredKeyedService<ILedgerService>("billing");
            var access = provider.GetRequiredKeyedService<ILedgerService>("access");
            Assert.Equal("", app.Name);
            Assert.Equal("billing", billing.Name);

            await app.AddEntryAsync("APP", new AuditedPayload());
            var invoice = await billing.AddEntryAsync("INVOICE", new AuditedPayload { Description = "F-1" });
            await billing.AddEntryAsync("INVOICE", new AuditedPayload { Description = "F-2" });
            var login = await access.AddEntryAsync("LOGIN", new AuditedPayload { ActorId = "alice" });

            Assert.Equal(1, defaultStore.Count);
            Assert.Equal(2, billingStore.Count);
            Assert.Equal(1, accessStore.Count);
            Assert.Equal("billing-prod", invoice.LedgerId);
            Assert.Equal(1, invoice.Sequence);
            Assert.Equal("a1", login.KeyId);
            Assert.True((await app.VerifyAsync()).IsValid);
            Assert.True((await billing.VerifyAsync()).IsValid);
            Assert.True((await access.VerifyAsync()).IsValid);

            await StopAsync(provider, hosted);
        }

        [Fact]
        public async Task Named_ledgers_bind_from_their_own_configuration_section()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Provance:Ledgers:billing:LedgerId"] = "billing-from-config",
                    ["Provance:Ledgers:billing:SecretKey"] = LedgerHarness.Secret,
                    ["Provance:Ledgers:billing:MaxBatchSize"] = "16",
                    ["Provance:Ledgers:billing:Retention:KeepFor"] = "7.00:00:00"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance("billing", configuration.GetSection("Provance:Ledgers:billing")).UseInMemoryStore();
            await using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptionsMonitor<ProvanceOptions>>().Get("billing");
            Assert.Equal("billing-from-config", options.LedgerId);
            Assert.Equal(16, options.MaxBatchSize);
            Assert.Equal(TimeSpan.FromDays(7), options.Retention.KeepFor);
            Assert.Null(provider.GetService<ILedgerService>()); // no default ledger registered
        }

        [Fact]
        public void Invalid_options_name_the_ledger()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance("billing", o => o.SecretKey = LedgerHarness.Secret).UseInMemoryStore();
            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredKeyedService<ILedgerService>("billing"));
            Assert.Contains("Ledger 'billing'", ex.Message);
        }

        [Fact]
        public void Named_ledger_without_a_store_fails_with_an_explicit_message()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance("billing", o => { o.LedgerId = "b"; o.SecretKey = LedgerHarness.Secret; });
            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<ILedgerService>("billing"));
            Assert.Contains("ledger 'billing'", ex.Message);
        }

        [Fact]
        public void Registering_the_same_ledger_twice_does_not_start_it_twice()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance("billing", o => o.LedgerId = "b").UseInMemoryStore();
            services.AddProvance("billing", o => o.SecretKey = LedgerHarness.Secret);
            using var provider = services.BuildServiceProvider();

            Assert.Equal(4, provider.GetServices<IHostedService>().Count());
            var options = provider.GetRequiredService<IOptionsMonitor<ProvanceOptions>>().Get("billing");
            Assert.Equal("b", options.LedgerId);
            Assert.Equal(LedgerHarness.Secret, options.SecretKey);
        }

        [Fact]
        public async Task Event_types_starting_with_provance_are_reserved()
        {
            await using var harness = await LedgerHarness.StartAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Ledger.AddEntryAsync("provance.retention", new AuditedPayload()));
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Ledger.AddEntryAsync("PROVANCE.anything", new AuditedPayload()));
        }
    }
}
