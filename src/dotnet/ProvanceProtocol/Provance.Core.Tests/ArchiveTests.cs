using Microsoft.Extensions.DependencyInjection;
using Provance.Core.Comparison;
using Provance.Core.Cryptography;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services;
using Provance.Core.Services.Interfaces;
using Provance.Core.Signing;
using Provance.Core.Tests.TestSupport;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    /// <summary>The archive copy (<c>UseArchive</c>) and the field-level comparison between the two copies.</summary>
    public class ArchiveTests
    {
        private static Task<LedgerHarness> StartAsync(InMemoryLedgerStore primary, ILedgerStore archive)
            => LedgerHarness.StartAsync(primary, build: b => b.UseArchive(archive));

        private static async Task<LedgerHarness> StartWithEntriesAsync(InMemoryLedgerStore primary, InMemoryLedgerStore archive, int count)
        {
            var harness = await StartAsync(primary, archive);
            for (int i = 1; i <= count; i++)
            {
                await harness.Ledger.AddEntryAsync("REFUND", new AuditedPayload
                {
                    ActorId = $"support-{i}",
                    Data = new Dictionary<string, object?> { ["orderId"] = $"A-{1000 + i}", ["amount"] = "49.90" }
                });
            }
            return harness;
        }

        private static async Task WaitForArchiveAsync(InMemoryLedgerStore archive, int count)
        {
            for (int i = 0; i < 500 && archive.Count < count; i++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(count, archive.Count);
        }

        [Fact]
        public async Task Every_entry_is_copied_to_the_archive_and_verifies_there()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 5);

            await WaitForArchiveAsync(archive, 5);

            var verifier = new LedgerVerifier(archive, new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId);
            Assert.True((await verifier.VerifyAsync()).IsValid);
            var comparison = await harness.Ledger.CompareWithArchiveAsync();
            Assert.True(comparison.IsIdentical, comparison.Describe());
            Assert.Equal(5, comparison.Compared);
            var status = harness.Ledger.GetArchiveStatus()!;
            Assert.True(status.IsHealthy);
            Assert.Equal(5, status.ArchivedThrough);
        }

        [Fact]
        public async Task Entries_written_just_before_shutdown_are_copied_when_the_host_stops()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            var harness = await LedgerHarness.StartAsync(primary, configure: o => o.ArchiveInterval = TimeSpan.FromHours(1), build: b => b.UseArchive(archive));
            for (int i = 0; i < 50; i++)
            {
                _ = harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = $"#{i}" });
            }

            await harness.DisposeAsync();

            Assert.Equal(50, primary.Count);
            Assert.Equal(50, archive.Count);
        }

        [Fact]
        public async Task Copy_resumes_where_it_stopped_after_a_restart()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using (var first = await StartWithEntriesAsync(primary, archive, 3))
            {
                await WaitForArchiveAsync(archive, 3);
            }

            await using var second = await StartAsync(primary, archive);
            await second.Ledger.AddEntryAsync("LATER", new AuditedPayload());
            await WaitForArchiveAsync(archive, 4);
            Assert.True((await second.Ledger.CompareWithArchiveAsync()).IsIdentical);
        }

        [Fact]
        public async Task A_changed_value_shows_its_original_from_the_archive()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 4);
            await WaitForArchiveAsync(archive, 4);

            primary.Tamper(2, e => e with { PayloadJson = e.PayloadJson!.Replace("\"49.90\"", "\"4990.00\"", StringComparison.Ordinal) });

            Assert.Equal(VerificationFailure.PayloadHashMismatch, (await harness.Ledger.VerifyAsync()).Failure);
            var comparison = await harness.Ledger.CompareWithArchiveAsync();
            Assert.False(comparison.IsIdentical);
            var difference = Assert.Single(comparison.Differences);
            Assert.Equal(2, difference.Sequence);
            Assert.Equal(EntryDifferenceKind.Changed, difference.Kind);
            Assert.Equal(OriginalCopy.Archive, difference.Original);
            var field = Assert.Single(difference.Fields);
            Assert.Equal("payload.data.amount", field.Field);
            Assert.Equal("\"49.90\"", field.ArchiveValue);
            Assert.Equal("\"4990.00\"", field.PrimaryValue);
            Assert.Contains("payload.data.amount: \"49.90\" -> \"4990.00\"", comparison.Describe());
        }

        [Fact]
        public async Task Envelope_fields_are_compared_too()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 2);
            await WaitForArchiveAsync(archive, 2);

            primary.Tamper(1, e => e with { EventType = "NOTHING_TO_SEE", Timestamp = e.Timestamp.AddDays(-1) });

            var difference = Assert.Single((await harness.Ledger.CompareWithArchiveAsync()).Differences);
            Assert.Equal(new[] { "eventType", "timestamp" }, difference.Fields.Select(f => f.Field));
            Assert.Equal("REFUND", difference.Fields[0].ArchiveValue);
        }

        [Fact]
        public async Task Deleted_and_truncated_entries_are_listed()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 6);
            await WaitForArchiveAsync(archive, 6);

            primary.Delete(e => e.Sequence == 3 || e.Sequence == 6);

            var comparison = await harness.Ledger.CompareWithArchiveAsync();
            Assert.Equal(new long[] { 3, 6 }, comparison.Differences.Select(d => d.Sequence));
            Assert.All(comparison.Differences, d => Assert.Equal(EntryDifferenceKind.MissingFromPrimary, d.Kind));
        }

        [Fact]
        public async Task Deleting_the_first_entries_without_retention_is_a_difference()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 5);
            await WaitForArchiveAsync(archive, 5);

            primary.Delete(e => e.Sequence <= 2);

            var verification = await harness.Ledger.VerifyAsync();
            Assert.Equal(VerificationFailure.SequenceGap, verification.Failure);
            Assert.Equal(1, verification.FailedAtSequence);

            var comparison = await harness.Ledger.CompareWithArchiveAsync();
            var difference = Assert.Single(comparison.Differences);
            Assert.Equal(EntryDifferenceKind.MissingFromPrimary, difference.Kind);
            Assert.Equal(1, difference.Sequence);
            Assert.Equal(2, difference.ThroughSequence);
        }

        [Fact]
        public async Task A_change_in_the_archive_is_attributed_to_the_archive()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 3);
            await WaitForArchiveAsync(archive, 3);

            archive.Tamper(3, e => e with { EventType = "EDITED_IN_THE_ARCHIVE" });

            var difference = Assert.Single((await harness.Ledger.CompareWithArchiveAsync()).Differences);
            Assert.Equal(OriginalCopy.Primary, difference.Original);
            Assert.Equal("REFUND", difference.EventType);
        }

        [Fact]
        public async Task A_rewrite_with_the_key_is_shown_as_both_copies_verifying()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 2);
            await WaitForArchiveAsync(archive, 2);

            var signer = new HmacLedgerSigner("k1", LedgerHarness.Secret);
            primary.Tamper(2, e =>
            {
                const string payload = "{\"actorId\":\"someone-else\"}";
                var forged = e with { PayloadJson = payload, PayloadHash = LedgerHashing.Sha256Hex(payload) };
                string hash = LedgerHashing.ComputeEntryHash(forged);
                byte[] signature = signer.SignAsync(LedgerHashing.HashToBytes(hash)).AsTask().Result;
                return forged with { Hash = hash, Signature = Convert.ToHexString(signature).ToLowerInvariant() };
            });

            Assert.True((await harness.Ledger.VerifyAsync()).IsValid); // the chain alone cannot tell
            var difference = Assert.Single((await harness.Ledger.CompareWithArchiveAsync()).Differences);
            Assert.Equal(OriginalCopy.BothVerify, difference.Original);
            Assert.Contains(difference.Fields, f => f.Field == "payload.actorId" && f.ArchiveValue == "\"support-2\"" && f.PrimaryValue == "\"someone-else\"");
            Assert.Contains(difference.Fields, f => f.Field == "payload.data.amount" && f.ArchiveValue == "\"49.90\"" && f.PrimaryValue == null);
        }

        [Fact]
        public async Task An_entry_modified_before_it_was_copied_is_not_copied()
        {
            var primary = new InMemoryLedgerStore();
            await using (var noArchive = await LedgerHarness.StartAsync(primary))
            {
                for (int i = 0; i < 4; i++) await noArchive.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = $"#{i}" });
            }
            primary.Tamper(3, e => e with { EventType = "FORGED" });

            var archive = new InMemoryLedgerStore();
            await using var harness = await StartAsync(primary, archive);
            await WaitForArchiveAsync(archive, 2);
            await harness.Ledger.CompareWithArchiveAsync();

            Assert.Equal(2, archive.Count); // entries 1 and 2 only
            var status = harness.Ledger.GetArchiveStatus()!;
            Assert.False(status.IsHealthy);
            Assert.Equal(2, status.ArchivedThrough);
            Assert.Contains("Entry 3", status.Problem);
        }

        [Fact]
        public async Task Redaction_erases_the_payload_in_both_copies()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 2);
            await WaitForArchiveAsync(archive, 2);
            var entry = (await primary.GetHeadAsync())!;

            Assert.True(await harness.Ledger.RedactPayloadAsync(entry.Id));

            Assert.Null((await primary.GetByIdAsync(entry.Id))!.PayloadJson);
            Assert.Null((await archive.GetByIdAsync(entry.Id))!.PayloadJson);
            Assert.True((await harness.Ledger.CompareWithArchiveAsync()).IsIdentical);
        }

        [Fact]
        public async Task A_payload_erased_in_one_copy_only_is_reported_but_is_not_tampering()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new InMemoryLedgerStore();
            await using var harness = await StartWithEntriesAsync(primary, archive, 2);
            await WaitForArchiveAsync(archive, 2);
            var entry = (await primary.GetHeadAsync())!;
            await primary.RedactPayloadAsync(entry.Id); // directly, not through the ledger

            var comparison = await harness.Ledger.CompareWithArchiveAsync();
            Assert.True(comparison.IsIdentical);
            Assert.Equal(EntryDifferenceKind.RedactedInOneCopy, Assert.Single(comparison.Differences).Kind);
        }

        [Fact]
        public async Task Redaction_reports_an_archive_that_could_not_be_erased()
        {
            var primary = new InMemoryLedgerStore();
            var archive = new RefusingRedactionStore();
            await using var harness = await StartAsync(primary, archive);
            var entry = await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload { ActorId = "alice" });
            await harness.Ledger.CompareWithArchiveAsync();

            var ex = await Assert.ThrowsAsync<LedgerArchiveException>(() => harness.Ledger.RedactPayloadAsync(entry.Id));
            Assert.Contains("not in the archive", ex.Message);
            Assert.Null((await primary.GetByIdAsync(entry.Id))!.PayloadJson);
        }

        [Fact]
        public async Task Archive_and_primary_cannot_be_the_same_store()
        {
            var store = new InMemoryLedgerStore();
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(store, store));
        }

        [Fact]
        public async Task Comparing_without_an_archive_explains_how_to_configure_one()
        {
            await using var harness = await LedgerHarness.StartAsync();
            Assert.Null(harness.Ledger.GetArchiveStatus());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Ledger.CompareWithArchiveAsync());
            Assert.Contains("UseArchive", ex.Message);
        }

        [Fact]
        public async Task An_export_file_can_be_compared_with_the_database()
        {
            var primary = new InMemoryLedgerStore();
            await using var harness = await LedgerHarness.StartAsync(primary);
            for (int i = 0; i < 3; i++) await harness.Ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = $"#{i}" });

            using var buffer = new MemoryStream();
            await Export.LedgerJsonLines.WriteAsync(primary, buffer);
            buffer.Position = 0;
            var entries = new List<LedgerEntry>();
            await foreach (var e in Export.LedgerJsonLines.ReadAsync(buffer)) entries.Add(e);
            var export = new Export.JsonLinesLedgerFile(entries);

            primary.Tamper(2, e => e with { EventType = "CHANGED" });

            var comparer = new LedgerComparer(new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId);
            var difference = Assert.Single((await comparer.CompareAsync(primary, export)).Differences);
            Assert.Equal(2, difference.Sequence);
            Assert.Equal(OriginalCopy.Archive, difference.Original);
        }

        /// <summary>An archive whose account cannot update rows.</summary>
        private sealed class RefusingRedactionStore : ILedgerStore
        {
            private readonly InMemoryLedgerStore _inner = new();

            public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default) => _inner.GetHeadAsync(cancellationToken);
            public Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default) => _inner.AppendAsync(entries, cancellationToken);
            public Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
            public IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, CancellationToken cancellationToken = default) => _inner.ReadAsync(fromSequence, cancellationToken);
            public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
                => throw new UnauthorizedAccessException("UPDATE permission denied on table provance_archive.");
        }
    }
}
