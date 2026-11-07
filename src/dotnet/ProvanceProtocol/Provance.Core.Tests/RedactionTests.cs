using Microsoft.Extensions.DependencyInjection;
using Provance.Core.Data;
using Provance.Core.Retention;
using Provance.Core.Services;
using Provance.Core.Signing;
using Provance.Core.Tests.TestSupport;
using Provance.Core.Verification;

namespace Provance.Core.Tests
{
    /// <summary>
    /// An erasure through the library is authorized by a signed redaction record; a payload set to null directly in
    /// the database is not, and verification reports it.
    /// </summary>
    public class RedactionTests
    {
        private static async Task<(LedgerHarness Harness, InMemoryLedgerStore Store, List<LedgerEntry> Entries)> StartAsync(int count, Action<IProvanceBuilder>? build = null)
        {
            var store = new InMemoryLedgerStore();
            var harness = await LedgerHarness.StartAsync(store, build: build);
            var entries = new List<LedgerEntry>();
            for (int i = 1; i <= count; i++)
            {
                entries.Add(await harness.Ledger.AddEntryAsync("PROFILE_UPDATED", new AuditedPayload { ActorId = $"user-{i}", Description = "email changed" }));
            }
            return (harness, store, entries);
        }

        [Fact]
        public async Task An_erasure_through_the_library_appends_a_record_and_verifies()
        {
            var (harness, store, entries) = await StartAsync(3);
            await using var _ = harness;

            Assert.True(await harness.Ledger.RedactPayloadAsync(entries[1].Id));

            var head = (await store.GetHeadAsync())!;
            Assert.Equal(LedgerRedactionRecord.EventType, head.EventType);
            Assert.True(LedgerRedactionRecord.TryParse(head.PayloadJson, out long sequence, out Guid id));
            Assert.Equal(2, sequence);
            Assert.Equal(entries[1].Id, id);
            Assert.DoesNotContain("user-2", head.PayloadJson); // the record names the entry, not its content

            var result = await harness.Ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.RedactedPayloads);
        }

        [Fact]
        public async Task A_payload_set_to_null_in_the_database_is_detected()
        {
            var (harness, store, entries) = await StartAsync(3);
            await using var _ = harness;

            await store.RedactPayloadAsync(entries[2].Id); // UPDATE provance_ledger SET payload = NULL WHERE seq = 3

            var result = await harness.Ledger.VerifyAsync();
            Assert.Equal(VerificationFailure.UnrecordedRedaction, result.Failure);
            Assert.Equal(3, result.FailedAtSequence);
        }

        [Fact]
        public async Task Erasing_again_through_the_library_does_not_authorize_a_deletion_made_in_the_database()
        {
            var (harness, store, entries) = await StartAsync(2);
            await using var _ = harness;
            await store.RedactPayloadAsync(entries[0].Id);

            Assert.True(await harness.Ledger.RedactPayloadAsync(entries[0].Id));

            Assert.Equal(2, store.Count); // no record appended
            Assert.Equal(VerificationFailure.UnrecordedRedaction, (await harness.Ledger.VerifyAsync()).Failure);
        }

        [Fact]
        public async Task A_record_authorizes_only_the_entry_it_names()
        {
            var (harness, store, entries) = await StartAsync(3);
            await using var _ = harness;
            await harness.Ledger.RedactPayloadAsync(entries[0].Id);

            await store.RedactPayloadAsync(entries[2].Id);

            var result = await harness.Ledger.VerifyAsync();
            Assert.Equal(VerificationFailure.UnrecordedRedaction, result.Failure);
            Assert.Equal(3, result.FailedAtSequence);
        }

        [Fact]
        public async Task Ledgers_erased_with_version_0_1_can_still_be_verified_on_request()
        {
            var (harness, store, entries) = await StartAsync(2);
            await using var _ = harness;
            await store.RedactPayloadAsync(entries[0].Id); // what 0.1 did: no record

            var result = await harness.Ledger.VerifyAsync(new LedgerVerificationOptions { AcceptUnrecordedRedactions = true });

            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.RedactedPayloads);
        }

        [Fact]
        public async Task Unknown_entries_are_not_recorded()
        {
            var (harness, store, _) = await StartAsync(1);
            await using var _h = harness;

            Assert.False(await harness.Ledger.RedactPayloadAsync(Guid.NewGuid()));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public async Task The_archive_receives_the_record_and_verifies_after_the_erasure()
        {
            var archive = new InMemoryLedgerStore();
            var (harness, _, entries) = await StartAsync(2, b => b.UseArchive(archive));
            await using var _h = harness;
            await harness.Ledger.CompareWithArchiveAsync();

            await harness.Ledger.RedactPayloadAsync(entries[0].Id);
            await harness.Ledger.CompareWithArchiveAsync();

            Assert.Null((await archive.GetByIdAsync(entries[0].Id))!.PayloadJson);
            var archived = await new LedgerVerifier(archive, new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId).VerifyAsync();
            Assert.True(archived.IsValid, archived.Message);
            Assert.Equal(3, archived.LastSequence);
        }

        [Fact]
        public async Task An_entry_already_removed_by_retention_is_erased_in_the_archive()
        {
            var archive = new InMemoryLedgerStore();
            var clock = new TestClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
            var store = new InMemoryLedgerStore();
            await using var harness = await LedgerHarness.StartAsync(store, build: b =>
            {
                b.Services.AddSingleton<TimeProvider>(clock);
                b.UseArchive(archive);
            });
            var old = await harness.Ledger.AddEntryAsync("PROFILE_UPDATED", new AuditedPayload { ActorId = "alice" });
            await harness.Ledger.AddEntryAsync("PROFILE_UPDATED", new AuditedPayload { ActorId = "bob" });
            clock.Advance(TimeSpan.FromDays(10));
            await harness.Ledger.AddEntryAsync("PROFILE_UPDATED", new AuditedPayload { ActorId = "carol" });
            Assert.True((await harness.Ledger.ApplyRetentionAsync(TimeSpan.FromDays(7))).Applied);
            Assert.Null(await store.GetByIdAsync(old.Id));

            Assert.True(await harness.Ledger.RedactPayloadAsync(old.Id));
            await harness.Ledger.CompareWithArchiveAsync();

            Assert.Null((await archive.GetByIdAsync(old.Id))!.PayloadJson);
            var archived = await new LedgerVerifier(archive, new HmacLedgerSigner("k1", LedgerHarness.Secret), LedgerHarness.LedgerId).VerifyAsync();
            Assert.True(archived.IsValid, archived.Message);
            Assert.True((await harness.Ledger.VerifyAsync()).IsValid);
        }
    }
}
