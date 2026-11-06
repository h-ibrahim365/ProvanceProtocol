using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Checkpoints;
using Provance.Core.Data;
using Provance.Core.Tests.TestSupport;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    public sealed class CheckpointTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "provance-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public async Task No_checkpoint_for_an_empty_ledger()
        {
            await using var harness = await LedgerHarness.StartAsync();
            Assert.Null(await harness.Ledger.CreateCheckpointAsync());
        }

        [Fact]
        public async Task Checkpoint_is_signed_and_references_the_head()
        {
            await using var harness = await LedgerHarness.StartAsync();
            var head = await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());

            var checkpoint = (await harness.Ledger.CreateCheckpointAsync())!;

            Assert.Equal(head.Sequence, checkpoint.Sequence);
            Assert.Equal(head.Hash, checkpoint.Hash);
            Assert.Null(await harness.Services.GetRequiredService<LedgerVerifier>().CheckCheckpointAsync(checkpoint));
        }

        [Fact]
        public async Task Checkpoint_json_round_trips()
        {
            await using var harness = await LedgerHarness.StartAsync();
            await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            var checkpoint = (await harness.Ledger.CreateCheckpointAsync())!;

            var parsed = LedgerCheckpoint.FromJson(checkpoint.ToJson());

            Assert.Equal(checkpoint, parsed);
            Assert.Contains("\"v\":1", checkpoint.ToJson());
        }

        [Fact]
        public async Task File_sink_writes_once_per_sequence_and_reads_the_latest()
        {
            await using var harness = await LedgerHarness.StartAsync();
            var sink = new FileCheckpointSink(_directory);

            await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            var first = (await harness.Ledger.CreateCheckpointAsync())!;
            await sink.PublishAsync(first);
            await sink.PublishAsync(first with { Signature = new string('0', 64) }); // ignored, never overwritten

            await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            var second = (await harness.Ledger.CreateCheckpointAsync())!;
            await sink.PublishAsync(second);

            var latest = await FileCheckpointSink.ReadLatestAsync(_directory, LedgerHarness.LedgerId);
            Assert.Equal(second, latest);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, LedgerHarness.LedgerId)).Length);

            string firstFile = Directory.GetFiles(Path.Combine(_directory, LedgerHarness.LedgerId)).Order().First();
            Assert.Equal(first, LedgerCheckpoint.FromJson(await File.ReadAllTextAsync(firstFile)));
        }

        [Fact]
        public async Task Publisher_publishes_only_when_the_head_moves()
        {
            var sink = new RecordingSink();
            await using var harness = await LedgerHarness.StartAsync(build: b => b.AddCheckpointSink(sink));
            var publisher = harness.Services.GetServices<IHostedService>().OfType<CheckpointPublisher>().Single();

            await publisher.PublishOnceAsync(CancellationToken.None); // empty ledger: nothing
            await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            await publisher.PublishOnceAsync(CancellationToken.None);
            await publisher.PublishOnceAsync(CancellationToken.None); // same head: nothing
            await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload());
            await publisher.PublishOnceAsync(CancellationToken.None);

            Assert.Equal(new long[] { 1, 2 }, sink.Published.Select(c => c.Sequence));
        }

        private sealed class RecordingSink : ILedgerCheckpointSink
        {
            public List<LedgerCheckpoint> Published { get; } = [];

            public Task PublishAsync(LedgerCheckpoint checkpoint, CancellationToken cancellationToken = default)
            {
                Published.Add(checkpoint);
                return Task.CompletedTask;
            }
        }
    }
}
