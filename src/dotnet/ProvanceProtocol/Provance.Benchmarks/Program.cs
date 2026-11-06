using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Canonicalization;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

namespace Provance.Benchmarks
{
    /// <summary>End-to-end append cost with the in-memory store (sealing, signing, queueing; no I/O).</summary>
    [MemoryDiagnoser]
    public class AppendBenchmarks
    {
        private ServiceProvider _provider = null!;
        private List<IHostedService> _hosted = null!;
        private ILedgerService _ledger = null!;
        private readonly AuditedPayload _payload = new() { ActorId = "bench", Description = "benchmark entry" };

        [GlobalSetup]
        public async Task Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance(o =>
            {
                o.LedgerId = "benchmarks";
                o.SecretKey = "benchmark-secret-key-0123456789abcdef";
                o.QueueCapacity = 100_000;
            }).UseInMemoryStore();
            _provider = services.BuildServiceProvider();
            _hosted = _provider.GetServices<IHostedService>().ToList();
            foreach (var service in _hosted) await service.StartAsync(CancellationToken.None);
            _ledger = _provider.GetRequiredService<ILedgerService>();
        }

        [GlobalCleanup]
        public async Task Cleanup()
        {
            foreach (var service in _hosted) await service.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }

        [Benchmark(Description = "1 append, awaited")]
        public Task<LedgerEntry> SingleAppend() => _ledger.AddEntryAsync("BENCH", _payload);

        [Benchmark(Description = "1000 concurrent appends", OperationsPerInvoke = 1000)]
        public Task Burst1000() => Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => _ledger.AddEntryAsync("BENCH", _payload)));
    }

    /// <summary>Cost of RFC 8785 canonicalization for a typical payload.</summary>
    [MemoryDiagnoser]
    public class CanonicalizationBenchmarks
    {
        private readonly string _json = """{"orderId":"A-1001","amount":"49.90","items":[{"sku":"X1","qty":2},{"sku":"Y7","qty":1}],"customer":{"id":"c-42","country":"BE"},"ratio":0.3333333333333333}""";

        [Benchmark]
        public string Canonicalize() => JsonCanonicalizer.Canonicalize(_json);
    }
}
