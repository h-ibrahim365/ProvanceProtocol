using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Options;
using Provance.Benchmarks;
using Provance.Core.Data;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;

// This line initiates the benchmark runner
BenchmarkRunner.Run<ProvanceLoadTest>();

namespace Provance.Benchmarks
{
    // --- THE BENCHMARK CLASS ---
    [MemoryDiagnoser] // Measures memory allocation (RAM usage)
    public class ProvanceLoadTest
    {
        private LedgerService _ledgerService = null!;
        private AuditedPayload _payload = null!;

        // GlobalSetup runs once before the benchmarks
        [GlobalSetup]
        public void Setup()
        {
            // 1. Configure Protocol Options
            var options = Options.Create(new ProvanceOptions
            {
                GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000",
                SecretKey = "BENCHMARK_SECRET_KEY_123456789"
            });

            // 2. Use In-Memory Store Mock
            var store = new InMemoryLedgerStore();

            // 3. Initialize the Queue
            var queue = new EntryQueue();

            // In a real app, LedgerWriterService does this. 
            // Here, we must manually drain the queue to prevent it from filling up (100k limit).
            // If we don't do this, the benchmark will hang indefinitely due to Backpressure (Wait mode).
            Task.Run(async () =>
            {
                try
                {
                    // Continuously read and discard items to simulate a healthy storage layer
                    while (await queue.Reader.WaitToReadAsync())
                    {
                        while (queue.Reader.TryRead(out var _))
                        {
                            // Item consumed (simulating write to DB)
                        }
                    }
                }
                catch
                {
                    // Ignore cancellation during benchmark teardown
                }
            });

            // 4. Initialize the Ledger Service
            _ledgerService = new LedgerService(options, store, queue);

            // 5. Pre-allocate payload
            _payload = new AuditedPayload
            {
                ActorId = "Benchmarker",
                Description = "Stress Test"
            };
        }

        // SCENARIO 1: Pure latency
        [Benchmark]
        public async Task Single_Write()
        {
            await _ledgerService.AddEntryAsync("BENCH_EVENT", _payload);
        }

        // SCENARIO 2: Heavy Load (1000 concurrent)
        [Benchmark]
        public async Task HeavyLoad_Burst_1000()
        {
            var tasks = new Task[1000];

            for (int i = 0; i < 1000; i++)
            {
                tasks[i] = _ledgerService.AddEntryAsync("BURST_EVENT", _payload);
            }

            await Task.WhenAll(tasks);
        }
    }

    // Minimalist In-Memory Store Mock
    public class InMemoryLedgerStore : ILedgerStore
    {
        /// <inheritdoc />
        public Task WriteEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<LedgerEntry?>(null);

        /// <inheritdoc />
        public Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId, CancellationToken cancellationToken = default)
            => Task.FromResult<LedgerEntry?>(null);

        /// <inheritdoc />
        public Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<LedgerEntry>>([]);

        /// <inheritdoc />
        public Task<bool> AcquireOrRenewLeaseAsync(string resource, string workerId, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}