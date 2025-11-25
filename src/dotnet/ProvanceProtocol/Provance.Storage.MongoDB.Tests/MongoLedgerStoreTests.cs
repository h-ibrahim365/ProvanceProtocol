using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Provance.Core.Data;
using Provance.Storage.MongoDB;
using Testcontainers.MongoDb;
using Xunit;

namespace Provance.Storage.MongoDB.Tests
{
    public class MongoLedgerStoreTests : IAsyncLifetime
    {
        private readonly MongoDbContainer _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:7.0")
            .Build();

        private MongoLedgerStore _store = null!;

        public async Task InitializeAsync()
        {
            await _mongoContainer.StartAsync();

            try
            {
                _ = BsonSerializer.SerializerRegistry.GetSerializer<Guid>();
            }
            catch
            {
                BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            }

            if (!BsonClassMap.IsClassMapRegistered(typeof(LedgerEntry)))
            {
                BsonClassMap.RegisterClassMap<LedgerEntry>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(c => c.Id);
                    cm.MapMember(c => c.Timestamp)
                        .SetSerializer(new DateTimeOffsetSerializer(BsonType.String));
                });
            }

            var options = Options.Create(new MongoDbOptions
            {
                ConnectionString = _mongoContainer.GetConnectionString(),
                DatabaseName = "integration_test_db",
                CollectionName = "ledger_entries"
            });

            _store = new MongoLedgerStore(options);
        }

        public async Task DisposeAsync()
        {
            await _mongoContainer.DisposeAsync();
        }

        [Fact]
        public async Task WriteEntryAsync_ShouldPersistData_AndRetrieveById()
        {
            // Arrange
            var ct = CancellationToken.None;

            var entryId = Guid.NewGuid();
            var entry = new LedgerEntry
            {
                Id = entryId,
                Timestamp = DateTimeOffset.UtcNow,
                EventType = "TEST_WRITE",
                PreviousHash = "GENESIS_MOCK",
                Payload = new AuditedPayload { ActorId = "Tester", Description = "Integration Test Payload" },
                CurrentHash = "HASH_123"
            };

            // Act
            await _store.WriteEntryAsync(entry, ct);
            var retrievedEntry = await _store.GetEntryByIdAsync(entryId, ct);

            // Assert
            Assert.NotNull(retrievedEntry);
            Assert.Equal(entryId, retrievedEntry!.Id);
            Assert.Equal("TEST_WRITE", retrievedEntry.EventType);
            Assert.Equal("Tester", retrievedEntry.Payload.ActorId);
        }

        [Fact]
        public async Task GetLastEntryAsync_ShouldReturn_TheMostRecentEntry()
        {
            // Arrange
            var ct = CancellationToken.None;

            var oldEntry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
                EventType = "OLD_EVENT",
                PreviousHash = "A",
                Payload = new AuditedPayload(),
                CurrentHash = "A"
            };

            var newEntry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                EventType = "NEW_EVENT",
                PreviousHash = "B",
                Payload = new AuditedPayload(),
                CurrentHash = "B"
            };

            await _store.WriteEntryAsync(oldEntry, ct);
            await _store.WriteEntryAsync(newEntry, ct);

            // Act
            var lastEntry = await _store.GetLastEntryAsync(ct);

            // Assert
            Assert.NotNull(lastEntry);
            Assert.Equal(newEntry.Id, lastEntry!.Id);
            Assert.Equal("NEW_EVENT", lastEntry.EventType);
        }

        [Fact]
        public async Task GetAllEntriesAsync_ShouldReturn_ChronologicalOrder()
        {
            // Arrange
            var ct = CancellationToken.None;

            var entry1 = new LedgerEntry { Id = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(1), EventType = "1", PreviousHash = "", Payload = new AuditedPayload(), CurrentHash = "" };
            var entry2 = new LedgerEntry { Id = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(2), EventType = "2", PreviousHash = "", Payload = new AuditedPayload(), CurrentHash = "" };
            var entry3 = new LedgerEntry { Id = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(3), EventType = "3", PreviousHash = "", Payload = new AuditedPayload(), CurrentHash = "" };

            await _store.WriteEntryAsync(entry2, ct);
            await _store.WriteEntryAsync(entry3, ct);
            await _store.WriteEntryAsync(entry1, ct);

            // Act
            var allEntries = (await _store.GetAllEntriesAsync(ct)).ToList();

            // Assert
            Assert.Equal(3, allEntries.Count);
            Assert.Equal("1", allEntries[0].EventType);
            Assert.Equal("2", allEntries[1].EventType);
            Assert.Equal("3", allEntries[2].EventType);
        }

        [Fact]
        public async Task GetEntryByIdAsync_ShouldReturnNull_WhenIdDoesNotExist()
        {
            // Arrange
            var ct = CancellationToken.None;

            // Act
            var result = await _store.GetEntryByIdAsync(Guid.NewGuid(), ct);

            // Assert
            Assert.Null(result);
        }
    }
}