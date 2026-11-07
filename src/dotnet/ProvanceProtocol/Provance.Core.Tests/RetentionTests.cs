using Microsoft.Extensions.DependencyInjection;
using Provance.Core.Data;
using Provance.Core.Retention;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;
using Provance.Core.Tests.TestSupport;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    /// <summary>Removing old entries from the primary store while the ledger stays verifiable.</summary>
    public class RetentionTests
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

        private sealed class Setup : IAsyncDisposable
        {
            public required LedgerHarness Harness { get; init; }
            public required InMemoryLedgerStore Primary { get; init; }
            public required InMemoryLedgerStore Archive { get; init; }
            public required TestClock Clock { get; init; }
            public ILedgerService Ledger => Harness.Ledger;

            public ValueTask DisposeAsync() => Harness.DisposeAsync();
        }

        /// <summary>Five entries on day 0, three on day 10.</summary>
        private static async Task<Setup> StartAsync(bool withArchive = true, Action<Options.ProvanceOptions>? configure = null)
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            var clock = new TestClock(Start);
            var harness = await LedgerHarness.StartAsync(primary, configure, b =>
            {
                b.Services.AddSingleton<TimeProvider>(clock);
                if (withArchive) b.UseArchive(archive);
            });

            for (int i = 1; i <= 8; i++)
            {
                if (i == 6) clock.Advance(TimeSpan.FromDays(10));
                await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = $"#{i}" });
            }
            return new Setup { Harness = harness, Primary = primary, Archive = archive, Clock = clock };
        }

        private static LedgerVerifier Verifier(ILedgerStore store)
            => new(store, new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId);

        [Fact]
        public async Task Old_entries_leave_the_primary_store_and_the_ledger_still_verifies()
        {
            await using var setup = await StartAsync();

            var result = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            Assert.True(result.Applied, result.Message);
            Assert.Equal(4, result.RemovedEntries); // entries 1 to 4; entry 5, the last old one, stays as the anchor
            Assert.Equal(5, result.FirstSequence);
            Assert.Equal(LedgerRetentionRecord.EventType, result.Record!.EventType);
            Assert.Equal(9, result.Record.Sequence);

            var verification = await setup.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = result.Checkpoint });
            Assert.True(verification.IsValid, verification.Message);
            Assert.Equal(5, verification.FirstSequence);
            Assert.Contains("removed by retention", verification.Message);

            // The archive keeps the whole chain, from the genesis entry.
            await setup.Ledger.CompareWithArchiveAsync();
            var archived = await Verifier(setup.Archive).VerifyAsync();
            Assert.True(archived.IsValid, archived.Message);
            Assert.Equal(1, archived.FirstSequence);
            Assert.Equal(9, archived.LastSequence);

            var comparison = await setup.Ledger.CompareWithArchiveAsync();
            Assert.True(comparison.IsIdentical, comparison.Describe());
            Assert.Equal(4, comparison.RemovedByRetention);
            Assert.Empty(comparison.Differences);
        }

        [Fact]
        public async Task Retention_can_run_again_and_moves_the_start_forward()
        {
            await using var setup = await StartAsync();
            Assert.True((await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7))).Applied);

            setup.Clock.Advance(TimeSpan.FromDays(10));
            await setup.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = "#10" });
            var second = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            Assert.True(second.Applied, second.Message);
            Assert.Equal(9, second.FirstSequence); // the first retention record is now the anchor
            var verification = await setup.Ledger.VerifyAsync();
            Assert.True(verification.IsValid, verification.Message);
            Assert.Equal(9, verification.FirstSequence);
            Assert.True((await setup.Ledger.CompareWithArchiveAsync()).IsIdentical);
        }

        [Fact]
        public async Task Nothing_is_removed_while_nothing_is_old_enough()
        {
            await using var setup = await StartAsync();

            var result = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(30));

            Assert.False(result.Applied);
            Assert.Equal(8, setup.Primary.Count);
        }

        [Fact]
        public async Task Nothing_is_removed_without_an_archive_unless_allowed()
        {
            await using var setup = await StartAsync(withArchive: false);
            var refused = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));
            Assert.False(refused.Applied);
            Assert.Contains("No archive", refused.Message);
            Assert.Equal(8, setup.Primary.Count);

            await using var allowed = await StartAsync(withArchive: false, configure: o => o.Retention.RequireArchive = false);
            var result = await allowed.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));
            Assert.True(result.Applied, result.Message);
            Assert.True((await allowed.Ledger.VerifyAsync()).IsValid);
        }

        [Fact]
        public async Task Nothing_is_removed_from_a_primary_store_that_does_not_verify()
        {
            await using var setup = await StartAsync();
            setup.Primary.Tamper(2, e => e with { EventType = "FORGED" });

            var result = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            Assert.False(result.Applied);
            Assert.Contains("does not verify", result.Message);
            Assert.Equal(8, setup.Primary.Count);
        }

        [Fact]
        public async Task Nothing_is_removed_when_the_archive_copy_differs()
        {
            await using var setup = await StartAsync();
            await setup.Ledger.CompareWithArchiveAsync();
            setup.Archive.Tamper(3, e => e with { EventType = "EDITED_IN_THE_ARCHIVE" });

            var result = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            Assert.False(result.Applied);
            Assert.Contains("identical copy", result.Message);
            Assert.Equal(8, setup.Primary.Count);
        }

        [Fact]
        public async Task Deleting_more_after_retention_is_still_detected()
        {
            await using var setup = await StartAsync();
            Assert.True((await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7))).Applied);

            setup.Primary.Delete(e => e.Sequence == 5); // the anchor

            var result = await setup.Ledger.VerifyAsync();
            Assert.False(result.IsValid);
            Assert.Equal(VerificationFailure.SequenceGap, result.Failure);
            Assert.Equal(1, result.FailedAtSequence);
        }

        [Fact]
        public async Task A_forged_retention_record_needs_the_key()
        {
            await using var setup = await StartAsync();
            setup.Primary.Delete(e => e.Sequence < 5);
            var anchor = await FirstAsync(setup.Primary);
            setup.Primary.Tamper(8, e => e with
            {
                EventType = LedgerRetentionRecord.EventType,
                PayloadJson = LedgerRetentionRecord.CreatePayload(anchor.Sequence, anchor.Hash)
            });

            var result = await setup.Ledger.VerifyAsync();

            Assert.False(result.IsValid);
            Assert.Equal(8, result.FailedAtSequence);
        }

        [Fact]
        public async Task A_checkpoint_older_than_the_retained_entries_is_rejected_with_an_explanation()
        {
            await using var setup = await StartAsync();
            var old = await setup.Ledger.CreateCheckpointAsync(); // sequence 8, day 10
            await setup.Ledger.AddEntryAsync("EVENT", new AuditedPayload()); // sequence 9, day 10
            setup.Clock.Advance(TimeSpan.FromDays(10));
            await setup.Ledger.AddEntryAsync("EVENT", new AuditedPayload()); // sequence 10, day 20
            var retention = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));
            Assert.True(retention.Applied, retention.Message);
            Assert.Equal(9, retention.FirstSequence);

            var result = await setup.Ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = old });

            Assert.Equal(VerificationFailure.CheckpointMismatch, result.Failure);
            Assert.Contains("removed by retention", result.Message);
        }

        [Fact]
        public async Task Retention_records_cannot_be_redacted()
        {
            await using var setup = await StartAsync();
            var result = await setup.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Ledger.RedactPayloadAsync(result.Record!.Id));
        }

        [Fact]
        public async Task Retention_runs_on_its_own_when_KeepFor_is_configured()
        {
            await using var setup = await StartAsync(configure: o =>
            {
                o.Retention.KeepFor = TimeSpan.FromDays(7);
                o.Retention.Interval = TimeSpan.FromMilliseconds(50);
            });

            for (int i = 0; i < 300 && setup.Primary.Count == 8; i++)
            {
                await Task.Delay(20);
            }

            Assert.Equal(5, (await FirstAsync(setup.Primary)).Sequence);
            Assert.True((await setup.Ledger.VerifyAsync()).IsValid);
        }

        [Fact]
        public async Task A_store_that_cannot_delete_is_reported()
        {
            await using var harness = await LedgerHarness.StartAsync(new FaultyStore(), configure: o => o.Retention.RequireArchive = false);
            await Assert.ThrowsAsync<NotSupportedException>(() => harness.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(1)));
        }

        private static async Task<LedgerEntry> FirstAsync(ILedgerStore store)
        {
            await foreach (var entry in store.ReadAsync())
            {
                return entry;
            }
            throw new InvalidOperationException("Empty store.");
        }
    }
}
