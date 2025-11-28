using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Provance.Core.Data;
using Testcontainers.MongoDb;

namespace Provance.Storage.MongoDB.Tests
{
    public class MongoLedgerStoreTests : IAsyncLifetime
    {
        private readonly MongoDbContainer _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:7.0")
            .Build();

        private MongoLedgerStore _store = null!;
        private MongoDbOptions _dbOptions = null!;

        public async Task InitializeAsync()
        {
            await _mongoContainer.StartAsync();

            try { _ = BsonSerializer.SerializerRegistry.GetSerializer<Guid>(); }
            catch { BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard)); }

            if (!BsonClassMap.IsClassMapRegistered(typeof(LedgerEntry)))
            {
                BsonClassMap.RegisterClassMap<LedgerEntry>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(c => c.Id);
                    cm.MapMember(c => c.Timestamp).SetSerializer(new DateTimeOffsetSerializer(BsonType.String));
                    cm.MapMember(c => c.Sequence);
                });
            }

            _dbOptions = new MongoDbOptions
            {
                ConnectionString = _mongoContainer.GetConnectionString(),
                DatabaseName = "integration_test_db",
                CollectionName = "ledger_entries"
            };

            _store = new MongoLedgerStore(Options.Create(_dbOptions));
        }

        public async Task DisposeAsync()
        {
            await _mongoContainer.DisposeAsync();
        }

        private async Task ClearCollectionAsync(CancellationToken ct)
        {
            var client = new MongoClient(_dbOptions.ConnectionString);
            var db = client.GetDatabase(_dbOptions.DatabaseName);
            var collection = db.GetCollection<LedgerEntry>(_dbOptions.CollectionName);

            await collection.DeleteManyAsync(Builders<LedgerEntry>.Filter.Empty, ct);
        }

        [Fact]
        public async Task WriteEntryAsync_ShouldPersistData_AndRetrieveById()
        {
            var ct = CancellationToken.None;
            await ClearCollectionAsync(ct);

            var entryId = Guid.NewGuid();
            var entry = new LedgerEntry
            {
                Id = entryId,
                Timestamp = DateTimeOffset.UtcNow,
                Sequence = 1,
                EventType = "TEST_WRITE",
                PreviousHash = "GENESIS_MOCK",
                Payload = new AuditedPayload { ActorId = "Tester", Description = "Integration Test Payload" },
                CurrentHash = "HASH_123"
            };

            await _store.WriteEntryAsync(entry, ct);
            var retrievedEntry = await _store.GetEntryByIdAsync(entryId, ct);

            Assert.NotNull(retrievedEntry);
            Assert.Equal(entryId, retrievedEntry!.Id);
            Assert.Equal("TEST_WRITE", retrievedEntry.EventType);
            Assert.Equal(1, retrievedEntry.Sequence);
            Assert.Equal("Tester", retrievedEntry.Payload.ActorId);
        }

        [Fact]
        public async Task GetLastEntryAsync_ShouldReturn_TheHighestSequenceEntry()
        {
            var ct = CancellationToken.None;
            await ClearCollectionAsync(ct);

            var entry1 = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
                Sequence = 1,
                EventType = "SEQ_1",
                PreviousHash = "A",
                Payload = new AuditedPayload(),
                CurrentHash = "A"
            };

            var entry2 = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddHours(-2),
                Sequence = 2,
                EventType = "SEQ_2",
                PreviousHash = "B",
                Payload = new AuditedPayload(),
                CurrentHash = "B"
            };

            await _store.WriteEntryAsync(entry1, ct);
            await _store.WriteEntryAsync(entry2, ct);

            var lastEntry = await _store.GetLastEntryAsync(ct);

            Assert.NotNull(lastEntry);
            Assert.Equal(entry2.Id, lastEntry!.Id);
            Assert.Equal(2, lastEntry.Sequence);
            Assert.Equal("SEQ_2", lastEntry.EventType);
        }

        [Fact]
        public async Task GetAllEntriesAsync_ShouldReturn_SequenceOrder()
        {
            var ct = CancellationToken.None;
            await ClearCollectionAsync(ct);

            var entry1 = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(3),
                Sequence = 1,
                EventType = "1",
                PreviousHash = "",
                Payload = new AuditedPayload(),
                CurrentHash = ""
            };

            var entry2 = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(1),
                Sequence = 2,
                EventType = "2",
                PreviousHash = "",
                Payload = new AuditedPayload(),
                CurrentHash = ""
            };

            var entry3 = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(2),
                Sequence = 3,
                EventType = "3",
                PreviousHash = "",
                Payload = new AuditedPayload(),
                CurrentHash = ""
            };

            await _store.WriteEntryAsync(entry2, ct);
            await _store.WriteEntryAsync(entry3, ct);
            await _store.WriteEntryAsync(entry1, ct);

            var allEntries = (await _store.GetAllEntriesAsync(ct)).ToList();

            Assert.Equal(3, allEntries.Count);
            Assert.Equal(1, allEntries[0].Sequence);
            Assert.Equal(2, allEntries[1].Sequence);
            Assert.Equal(3, allEntries[2].Sequence);
        }

        [Fact]
        public async Task GetEntryByIdAsync_ShouldReturnNull_WhenIdDoesNotExist()
        {
            var ct = CancellationToken.None;
            await ClearCollectionAsync(ct);

            var result = await _store.GetEntryByIdAsync(Guid.NewGuid(), ct);

            Assert.Null(result);
        }
    }
}