using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.MongoDB
{
    public class MongoLedgerStore : ILedgerStore
    {
        private readonly IMongoCollection<LedgerEntry> _collection;

        public MongoLedgerStore(IOptions<MongoDbOptions> options)
        {
            var mongoClient = new MongoClient(options.Value.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(options.Value.DatabaseName);
            _collection = mongoDatabase.GetCollection<LedgerEntry>(options.Value.CollectionName);

            // OPTIMIZATION: Index creation
            // Create a descending index on Timestamp to make GetLastEntryAsync ultra-fast.
            var indexKeysDefinition = Builders<LedgerEntry>.IndexKeys.Descending(x => x.Timestamp);
            var indexModel = new CreateIndexModel<LedgerEntry>(indexKeysDefinition);
            _collection.Indexes.CreateOne(indexModel);
        }

        public async Task WriteEntryAsync(LedgerEntry entry)
        {
            // MongoDB handles C# Guids automatically if configured, 
            // or BSON mapping can be forced globally at startup.
            await _collection.InsertOneAsync(entry);
        }

        public async Task<LedgerEntry?> GetLastEntryAsync()
        {
            // Sort by descending Timestamp and take the first one.
            return await _collection.Find(Builders<LedgerEntry>.Filter.Empty)
                .SortByDescending(e => e.Timestamp)
                .FirstOrDefaultAsync();
        }

        public async Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId)
        {
            return await _collection.Find(e => e.Id == entryId).FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync()
        {
            // Strict chronological order is required for chain verification.
            return await _collection.Find(Builders<LedgerEntry>.Filter.Empty)
                .SortBy(e => e.Timestamp)
                .ToListAsync();
        }
    }
}