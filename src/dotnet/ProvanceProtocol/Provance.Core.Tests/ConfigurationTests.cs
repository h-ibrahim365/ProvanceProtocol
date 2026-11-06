using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;
using Provance.Core.Tests.TestSupport;

namespace Provance.Core.Tests
{
    public class ConfigurationTests
    {
        [Fact]
        public void Missing_ledger_id_is_rejected()
        {
            using var provider = Build(o => o.LedgerId = "");
            var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProvanceOptions>>().Value);
            Assert.Contains("LedgerId", ex.Message);
        }

        [Theory]
        [InlineData("has space")]
        [InlineData("slash/not/allowed")]
        public void Invalid_ledger_id_is_rejected(string ledgerId)
        {
            using var provider = Build(o => o.LedgerId = ledgerId);
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProvanceOptions>>().Value);
        }

        [Fact]
        public void Uppercase_or_short_genesis_hash_is_rejected()
        {
            using var provider = Build(o => o.GenesisHash = new string('A', 64));
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProvanceOptions>>().Value);
        }

        [Theory]
        [InlineData((string?)null)]
        [InlineData("")]
        [InlineData("too-short")]
        [InlineData("DEFAULT_INSECURE_KEY_CHANGE_ME")]
        public void Weak_or_placeholder_secret_is_rejected(string? secret)
        {
            Assert.Throws<InvalidOperationException>(() => new HmacLedgerSigner("k1", secret));
        }

        [Fact]
        public void Signer_cannot_reuse_the_signing_key_id_as_a_retired_key()
        {
            var retired = new Dictionary<string, string> { ["k1"] = LedgerHarness.Secret };
            Assert.Throws<InvalidOperationException>(() => new HmacLedgerSigner("k1", LedgerHarness.Secret, retired));
        }

        [Fact]
        public void No_store_configured_fails_with_an_explicit_message()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance(o =>
            {
                o.LedgerId = "x";
                o.SecretKey = LedgerHarness.Secret;
            });
            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ILedgerService>());
            Assert.Contains("No PROVANCE store is configured", ex.Message);
        }

        [Fact]
        public async Task Explicit_genesis_hash_is_used_for_the_first_entry()
        {
            string genesis = new('a', 64);
            await using var harness = await LedgerHarness.StartAsync(configure: o => o.GenesisHash = genesis);
            var entry = await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            Assert.Equal(genesis, entry.PreviousHash);
            Assert.True((await harness.Ledger.VerifyAsync()).IsValid);
        }

        [Fact]
        public async Task Event_type_is_validated()
        {
            await using var harness = await LedgerHarness.StartAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Ledger.AddEntryAsync(" ", new AuditedPayload()));
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Ledger.AddEntryAsync(new string('x', 201), new AuditedPayload()));
        }

        [Fact]
        public async Task Unserializable_payload_is_rejected_before_queueing()
        {
            await using var harness = await LedgerHarness.StartAsync();
            var cyclic = new Node();
            cyclic.Next = cyclic;

            await Assert.ThrowsAsync<FormatException>(() => harness.Ledger.AddEntryAsync("EVENT", cyclic));
            Assert.Null(await harness.Ledger.GetHeadAsync());
        }

        [Fact]
        public async Task AddProvance_binds_from_configuration()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Provance:LedgerId"] = "from-config",
                    ["Provance:SecretKey"] = LedgerHarness.Secret,
                    ["Provance:MaxBatchSize"] = "32",
                    ["Provance:RetiredKeys:k0"] = "a-retired-unit-test-secret-0123456789abcdef"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance(configuration.GetSection(ProvanceOptions.SectionName)).UseInMemoryStore();
            await using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<ProvanceOptions>>().Value;
            Assert.Equal("from-config", options.LedgerId);
            Assert.Equal(32, options.MaxBatchSize);
            Assert.True(options.RetiredKeys.ContainsKey("k0"));
            Assert.Equal("k1", provider.GetRequiredService<ILedgerSigner>().KeyId);
        }

        private static ServiceProvider Build(Action<ProvanceOptions> configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance(o =>
            {
                o.LedgerId = "valid-id";
                o.SecretKey = LedgerHarness.Secret;
                configure(o);
            }).UseInMemoryStore();
            return services.BuildServiceProvider();
        }

        private sealed class Node
        {
            public Node? Next { get; set; }
        }
    }
}
