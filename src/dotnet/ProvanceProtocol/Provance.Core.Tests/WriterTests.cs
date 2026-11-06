using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Options;
using Provance.Core.Services;
using Provance.Core.Tests.TestSupport;

namespace Provance.Core.Tests
{
    /// <summary>Behavior of the writer under failures, concurrency and shutdown.</summary>
    public class WriterTests
    {
        private static AuditedPayload Payload(int i) => new() { Description = $"#{i}" };

        private static async Task AssertChainAsync(LedgerHarness harness, int expectedCount)
        {
            var result = await harness.Ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(expectedCount, result.EntriesVerified);
        }

        [Fact]
        public async Task Lost_acknowledgement_does_not_wedge_the_writer()
        {
            // Regression: in 0.0.3 a write that succeeded on the server but timed out on the client made every later append fail.
            var store = new FaultyStore { OnAppend = call => call == 1 ? AppendFault.FailAfterPersist : AppendFault.None };
            await using var harness = await LedgerHarness.StartAsync(store);

            var results = new List<LedgerEntry>();
            for (int i = 1; i <= 5; i++)
            {
                results.Add(await harness.Ledger.AddEntryAsync("EVENT", Payload(i)));
            }

            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, results.Select(r => r.Sequence));
            Assert.Equal(5, store.Inner.Count);
            await AssertChainAsync(harness, 5);
        }

        [Fact]
        public async Task Lost_acknowledgement_followed_by_a_failed_lookup_is_still_resolved()
        {
            var store = new FaultyStore
            {
                OnAppend = call => call == 0 ? AppendFault.FailAfterPersist : AppendFault.None,
                FailGetById = call => call == 0
            };
            await using var harness = await LedgerHarness.StartAsync(store);

            var entry = await harness.Ledger.AddEntryAsync("EVENT", Payload(1));
            var next = await harness.Ledger.AddEntryAsync("EVENT", Payload(2));

            Assert.Equal(1, entry.Sequence);
            Assert.Equal(2, next.Sequence);
            Assert.Equal(2, store.Inner.Count);
            await AssertChainAsync(harness, 2);
        }

        [Fact]
        public async Task Partially_persisted_batch_is_completed_without_duplicates()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new FaultyStore
            {
                OnAppend = call => call == 1 ? AppendFault.PersistFirstThenFail : AppendFault.None,
                AppendGate = async ct => { await gate.Task.WaitAsync(ct); }
            };
            await using var harness = await LedgerHarness.StartAsync(store);

            // The first append blocks; the next 20 entries pile up and form one batch.
            var first = harness.Ledger.AddEntryAsync("EVENT", Payload(0));
            await WaitUntilAsync(() => store.AppendCalls == 1);
            var others = Enumerable.Range(1, 20).Select(i => harness.Ledger.AddEntryAsync("EVENT", Payload(i))).ToList();
            await Task.Delay(50);
            gate.SetResult();

            await first;
            var entries = await Task.WhenAll(others);

            Assert.Equal(20, entries.Select(e => e.Sequence).Distinct().Count());
            Assert.Equal(21, store.Inner.Count);
            await AssertChainAsync(harness, 21);
        }

        [Fact]
        public async Task Transient_failures_are_retried()
        {
            var store = new FaultyStore { OnAppend = call => call < 2 ? AppendFault.FailBefore : AppendFault.None };
            await using var harness = await LedgerHarness.StartAsync(store);

            var entry = await harness.Ledger.AddEntryAsync("EVENT", Payload(1));

            Assert.Equal(1, entry.Sequence);
            Assert.Equal(3, store.AppendCalls);
        }

        [Fact]
        public async Task Persistent_failure_reaches_the_caller_and_the_writer_recovers()
        {
            var store = new FaultyStore { OnAppend = call => call < 3 ? AppendFault.FailBefore : AppendFault.None };
            await using var harness = await LedgerHarness.StartAsync(store, o => o.MaxAppendAttempts = 3);

            var ex = await Assert.ThrowsAsync<LedgerUnavailableException>(() => harness.Ledger.AddEntryAsync("EVENT", Payload(1)));
            Assert.IsType<TimeoutException>(ex.InnerException);

            var entry = await harness.Ledger.AddEntryAsync("EVENT", Payload(2));
            Assert.Equal(1, entry.Sequence);
            await AssertChainAsync(harness, 1);
        }

        [Fact]
        public async Task Concurrent_producers_produce_a_linear_chain()
        {
            await using var harness = await LedgerHarness.StartAsync();

            var entries = await Task.WhenAll(Enumerable.Range(0, 2000)
                .Select(i => Task.Run(() => harness.Ledger.AddEntryAsync("EVENT", Payload(i)))));

            Assert.Equal(Enumerable.Range(1, 2000).Select(i => (long)i), entries.Select(e => e.Sequence).Order());
            await AssertChainAsync(harness, 2000);
        }

        [Fact]
        public async Task Two_application_instances_can_share_one_ledger()
        {
            // Regression: in 0.0.3 the second instance crashed at startup because of the writer lease.
            var shared = new InMemoryLedgerStore();
            await using var instanceA = await LedgerHarness.StartAsync(shared, o => o.MaxBatchSize = 8);
            await using var instanceB = await LedgerHarness.StartAsync(shared, o => o.MaxBatchSize = 8);

            var writes = Enumerable.Range(0, 500).SelectMany(i => new[]
            {
                Task.Run(() => instanceA.Ledger.AddEntryAsync("FROM_A", Payload(i))),
                Task.Run(() => instanceB.Ledger.AddEntryAsync("FROM_B", Payload(i)))
            }).ToList();
            var entries = await Task.WhenAll(writes);

            Assert.Equal(1000, entries.Select(e => e.Sequence).Distinct().Count());
            Assert.Equal(1000, shared.Count);
            await AssertChainAsync(instanceA, 1000);
        }

        [Fact]
        public async Task Shutdown_drains_queued_entries()
        {
            var store = new FaultyStore { AppendGate = ct => Task.Delay(5, ct) };
            var harness = await LedgerHarness.StartAsync(store, o => o.MaxBatchSize = 1);

            var pending = Enumerable.Range(0, 50).Select(i => harness.Ledger.AddEntryAsync("EVENT", Payload(i))).ToList();
            await harness.DisposeAsync();

            var entries = await Task.WhenAll(pending);
            Assert.Equal(50, entries.Length);
            Assert.Equal(50, store.Inner.Count);
        }

        [Fact]
        public async Task Shutdown_timeout_fails_pending_entries_instead_of_hanging()
        {
            var store = new FaultyStore { AppendGate = ct => Task.Delay(Timeout.Infinite, ct) };
            var harness = await LedgerHarness.StartAsync(store);

            var pending = Enumerable.Range(0, 5).Select(i => harness.Ledger.AddEntryAsync("EVENT", Payload(i))).ToList();
            await WaitUntilAsync(() => store.AppendCalls == 1);
            await harness.StopAsync(TimeSpan.FromMilliseconds(200));

            foreach (var task in pending)
            {
                await Assert.ThrowsAsync<LedgerUnavailableException>(() => task);
            }
            await harness.DisposeAsync();
        }

        [Fact]
        public async Task Entries_are_refused_once_the_writer_is_stopped()
        {
            var harness = await LedgerHarness.StartAsync();
            await harness.StopAsync();

            await Assert.ThrowsAsync<LedgerUnavailableException>(() => harness.Ledger.AddEntryAsync("EVENT", Payload(1)));
            await harness.DisposeAsync();
        }

        [Fact]
        public async Task Full_queue_fails_fast_when_configured()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new FaultyStore { AppendGate = async ct => { await gate.Task.WaitAsync(ct); } };
            await using var harness = await LedgerHarness.StartAsync(store, o =>
            {
                o.QueueCapacity = 1;
                o.QueueFullBehavior = QueueFullBehavior.FailFast;
            });

            var inWriter = harness.Ledger.AddEntryAsync("EVENT", Payload(1));
            await WaitUntilAsync(() => store.AppendCalls == 1);
            var inQueue = harness.Ledger.AddEntryAsync("EVENT", Payload(2));

            await Assert.ThrowsAsync<LedgerOverloadedException>(() => harness.Ledger.AddEntryAsync("EVENT", Payload(3)));

            gate.SetResult();
            await inWriter;
            await inQueue;
        }

        [Fact]
        public async Task Writer_refuses_a_store_that_holds_another_ledger()
        {
            var store = new InMemoryLedgerStore();
            await using (var other = await LedgerHarness.StartAsync(store, o => o.LedgerId = "another-ledger"))
            {
                await other.Ledger.AddEntryAsync("EVENT", Payload(1));
            }

            await using var harness = await LedgerHarness.StartAsync(store, o => o.MaxAppendAttempts = 2);
            var ex = await Assert.ThrowsAsync<LedgerUnavailableException>(() => harness.Ledger.AddEntryAsync("EVENT", Payload(2)));
            Assert.Contains("another-ledger", ex.InnerException!.Message);
        }

        [Fact]
        public async Task Canceling_the_caller_does_not_corrupt_the_chain()
        {
            await using var harness = await LedgerHarness.StartAsync();
            using var cts = new CancellationTokenSource();
            var tasks = Enumerable.Range(0, 200).Select(i => harness.Ledger.AddEntryAsync("EVENT", Payload(i), cts.Token)).ToList();
            cts.Cancel();

            foreach (var task in tasks)
            {
                try { await task; } catch (OperationCanceledException) { }
            }
            await harness.Ledger.AddEntryAsync("EVENT", Payload(999));

            var result = await harness.Ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
                await Task.Delay(5);
            }
        }
    }
}
