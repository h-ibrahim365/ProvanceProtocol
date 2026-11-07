using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Comparison;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services.Interfaces;
using Provance.Core.Verification;

namespace Provance.Storage.EntityFrameworkCore.Tests
{
    /// <summary>
    /// Tests every database provider must pass. Each derived class supplies a provider; every test uses its
    /// own table so tests do not interfere.
    /// </summary>
    public abstract class StoreContractTests
    {
        private const string Secret = "ef-store-test-secret-key-0123456789abcdef";
        private const string LedgerId = "ef-test";

        /// <summary>Configures the provider for the database under test.</summary>
        protected abstract void ConfigureDatabase(DbContextOptionsBuilder db);

        /// <summary>Concurrent writes per instance in the two-instance test (lower for SQLite, which serializes writers).</summary>
        protected virtual int ConcurrentWritesPerInstance => 50;

        private static string NewTable() => "t_" + Guid.NewGuid().ToString("N")[..12];

        private async Task<Instance> StartAsync(string table, int readBatchSize = 1000, Action<IProvanceBuilder>? build = null, Action<IServiceCollection>? register = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var builder = services.AddProvance(o =>
            {
                o.LedgerId = LedgerId;
                o.SecretKey = Secret;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(5);
                o.MaxAppendAttempts = 10;
            }).UseEntityFrameworkCore(ConfigureDatabase, o =>
            {
                o.TableName = table;
                o.ReadBatchSize = readBatchSize;
            });
            build?.Invoke(builder);
            register?.Invoke(services);

            var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
            return new Instance(provider, hosted);
        }

        private static LedgerEntry Entry(long sequence, Guid? id = null) => new()
        {
            FormatVersion = 1,
            LedgerId = LedgerId,
            Sequence = sequence,
            Id = id ?? Guid.NewGuid(),
            Timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            EventType = "E",
            PayloadJson = "{}",
            PayloadHash = new string('a', 64),
            PreviousHash = new string('b', 64),
            KeyId = "k1",
            Hash = new string('c', 64),
            Signature = new string('d', 64)
        };

        [Fact]
        public async Task Schema_is_created_and_entries_round_trip_exactly()
        {
            await using var instance = await StartAsync(NewTable());
            var ledger = instance.Ledger;

            var first = await ledger.AddEntryAsync("A", new AuditedPayload
            {
                ActorId = "user-1",
                Data = new Dictionary<string, object?> { ["amount"] = "49.90", ["ratio"] = 0.1 + 0.2, ["big"] = 1234567890123L }
            });
            await ledger.AddEntryAsync("B", new { text = "Caf\u00e9 \u20ac \"quoted\" \\ \u0001", emoji = "\ud83d\ude00" });
            var last = await ledger.AddEntryAsync("C", new AuditedPayload { Description = "third" });

            Assert.Equal(first, await ledger.GetEntryAsync(first.Id));
            Assert.Equal(last, await ledger.GetHeadAsync());

            var result = await ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(3, result.EntriesVerified);
        }

        [Fact]
        public async Task Duplicate_sequence_or_id_is_reported_as_a_conflict()
        {
            await using var instance = await StartAsync(NewTable());
            var store = instance.Store;
            var existing = Entry(1);
            await store.AppendAsync([existing]);

            await Assert.ThrowsAsync<LedgerConcurrencyException>(() => store.AppendAsync([Entry(1)]));
            await Assert.ThrowsAsync<LedgerConcurrencyException>(() => store.AppendAsync([Entry(2, existing.Id)]));
        }

        [Fact]
        public async Task A_failed_batch_is_not_partially_persisted()
        {
            await using var instance = await StartAsync(NewTable());
            var store = instance.Store;
            await store.AppendAsync([Entry(1), Entry(2)]);
            await store.AppendAsync([Entry(4)]); // another writer already took sequence 4
            var fresh = Entry(3);

            // The batch 3, 4 conflicts on its second entry: the first one must not be persisted alone.
            await Assert.ThrowsAsync<LedgerConcurrencyException>(() => store.AppendAsync([fresh, Entry(4)]));

            Assert.Null(await store.GetByIdAsync(fresh.Id));
        }

        [Fact]
        public async Task Reading_streams_page_by_page()
        {
            await using var instance = await StartAsync(NewTable(), readBatchSize: 2);
            var store = instance.Store;
            await store.AppendAsync([Entry(1), Entry(2), Entry(3), Entry(4), Entry(5)]);

            var sequences = new List<long>();
            await foreach (var entry in store.ReadAsync(2)) sequences.Add(entry.Sequence);

            Assert.Equal(new long[] { 2, 3, 4, 5 }, sequences);
        }

        [Fact]
        public async Task Redaction_keeps_the_chain_valid()
        {
            await using var instance = await StartAsync(NewTable());
            var ledger = instance.Ledger;
            var personal = await ledger.AddEntryAsync("A", new AuditedPayload { ActorId = "jane@example.org" });
            await ledger.AddEntryAsync("B", new AuditedPayload());

            Assert.True(await ledger.RedactPayloadAsync(personal.Id));
            Assert.False(await ledger.RedactPayloadAsync(Guid.NewGuid()));

            Assert.True((await ledger.GetEntryAsync(personal.Id))!.IsRedacted);
            var result = await ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(1, result.RedactedPayloads);
        }

        [Fact]
        public async Task Two_instances_share_a_ledger_and_tampering_in_the_database_is_detected()
        {
            string table = NewTable();
            await using var instanceA = await StartAsync(table);
            await instanceA.Ledger.AddEntryAsync("INIT", new AuditedPayload()); // creates the table before B starts writing
            await using var instanceB = await StartAsync(table);

            int n = ConcurrentWritesPerInstance;
            var writes = Enumerable.Range(0, n).SelectMany(i => new[]
            {
                Task.Run(() => instanceA.Ledger.AddEntryAsync("FROM_A", new HttpLikePayload { Path = $"/a/{i}" })),
                Task.Run(() => instanceB.Ledger.AddEntryAsync("FROM_B", new HttpLikePayload { Path = $"/b/{i}" }))
            });
            var entries = await Task.WhenAll(writes);
            Assert.Equal(2 * n, entries.Select(e => e.Sequence).Distinct().Count());

            var valid = await instanceA.Ledger.VerifyAsync();
            Assert.True(valid.IsValid, valid.Message);
            Assert.Equal(2 * n + 1, valid.EntriesVerified);

            // Someone with database access edits one row directly.
            var target = entries.First(e => e.EventType == "FROM_B");
            await using (var scope = instanceA.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProvanceLedgerDbContext>();
                var row = await db.Entries.SingleAsync(r => r.Sequence == target.Sequence);
                row.PayloadJson = row.PayloadJson!.Replace("/b/", "/x/");
                await db.SaveChangesAsync();
            }

            var tampered = await instanceB.Ledger.VerifyAsync();
            Assert.Equal(VerificationFailure.PayloadHashMismatch, tampered.Failure);
            Assert.Equal(target.Sequence, tampered.FailedAtSequence);
        }

        [Fact]
        public async Task Entries_are_copied_to_an_archive_table_and_changes_show_their_original_value()
        {
            string table = NewTable();
            await using var instance = await StartAsync(table, build: b => b.UseEntityFrameworkCoreArchive(ConfigureDatabase, o => o.TableName = NewTable()));
            var ledger = instance.Ledger;
            for (int i = 1; i <= 3; i++)
            {
                await ledger.AddEntryAsync("REFUND", new AuditedPayload { Data = new Dictionary<string, object?> { ["amount"] = "49.90", ["order"] = i } });
            }

            var copied = await ledger.CompareWithArchiveAsync();
            Assert.True(copied.IsIdentical, copied.Describe());
            Assert.Equal(3, copied.Compared);

            await using (var scope = instance.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProvanceLedgerDbContext>();
                var row = await db.Entries.SingleAsync(r => r.Sequence == 2);
                row.PayloadJson = row.PayloadJson!.Replace("49.90", "4990.00");
                await db.SaveChangesAsync();
            }

            var comparison = await ledger.CompareWithArchiveAsync();
            var difference = Assert.Single(comparison.Differences);
            Assert.Equal(2, difference.Sequence);
            Assert.Equal(OriginalCopy.Archive, difference.Original);
            var field = Assert.Single(difference.Fields);
            Assert.Equal("payload.data.amount", field.Field);
            Assert.Equal("\"49.90\"", field.ArchiveValue);
        }

        [Fact]
        public async Task Retention_deletes_old_rows_and_the_ledger_still_verifies()
        {
            var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
            await using var instance = await StartAsync(NewTable(),
                build: b => b.UseEntityFrameworkCoreArchive(ConfigureDatabase, o => o.TableName = NewTable()),
                register: s => s.AddSingleton<TimeProvider>(clock));
            var ledger = instance.Ledger;
            for (int i = 1; i <= 6; i++)
            {
                if (i == 5) clock.Now = clock.Now.AddDays(10);
                await ledger.AddEntryAsync("EVENT", new AuditedPayload { Description = $"#{i}" });
            }

            var result = await ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));

            Assert.True(result.Applied, result.Message);
            Assert.Equal(3, result.RemovedEntries);
            Assert.Equal(4, result.FirstSequence);
            var verification = await ledger.VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = result.Checkpoint });
            Assert.True(verification.IsValid, verification.Message);
            Assert.Equal(4, verification.FirstSequence);
            var comparison = await ledger.CompareWithArchiveAsync();
            Assert.True(comparison.IsIdentical, comparison.Describe());
            Assert.Equal(3, comparison.RemovedByRetention);
        }

        [Fact]
        public async Task Named_ledgers_use_their_own_tables()
        {
            await using var instance = await StartAsync(NewTable(), register: s =>
                s.AddProvance("billing", o => { o.LedgerId = "ef-billing"; o.SecretKey = Secret; })
                 .UseEntityFrameworkCore(ConfigureDatabase, o => o.TableName = NewTable()));

            await instance.Ledger.AddEntryAsync("DEFAULT", new AuditedPayload());
            var billing = instance.Services.GetRequiredKeyedService<ILedgerService>("billing");
            var invoice = await billing.AddEntryAsync("INVOICE", new AuditedPayload());
            await billing.AddEntryAsync("INVOICE", new AuditedPayload());

            Assert.Equal(1, invoice.Sequence);
            Assert.True((await instance.Ledger.VerifyAsync()).IsValid);
            var billingResult = await billing.VerifyAsync();
            Assert.True(billingResult.IsValid, billingResult.Message);
            Assert.Equal(2, billingResult.EntriesVerified);
        }

        private sealed class ManualClock(DateTimeOffset start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = start;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed record HttpLikePayload : AuditedPayload
        {
            public string? Path { get; init; }
        }

        private sealed class Instance(ServiceProvider services, List<IHostedService> hosted) : IAsyncDisposable
        {
            public ServiceProvider Services { get; } = services;

            public ILedgerService Ledger => Services.GetRequiredService<ILedgerService>();

            public ILedgerStore Store => Services.GetRequiredService<ILedgerStore>();

            public async ValueTask DisposeAsync()
            {
                for (int i = hosted.Count - 1; i >= 0; i--) await hosted[i].StopAsync(CancellationToken.None);
                await Services.DisposeAsync();
            }
        }
    }
}
