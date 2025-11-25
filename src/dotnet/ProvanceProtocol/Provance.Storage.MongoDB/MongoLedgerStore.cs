using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.MongoDB
{
    /// <inheritdoc cref="ILedgerStore" />
    public class MongoLedgerStore : ILedgerStore
    {
        private readonly IMongoCollection<LedgerEntry> _collection;

        /// <inheritdoc cref="ILedgerStore" />
        public MongoLedgerStore(IOptions<MongoDbOptions> options)
        {
            var mongoClient = new MongoClient(options.Value.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(options.Value.DatabaseName);
            _collection = mongoDatabase.GetCollection<LedgerEntry>(options.Value.CollectionName);

            // OPTIMIZATION: Indexes
            var lastIndex = new CreateIndexModel<LedgerEntry>(
                Builders<LedgerEntry>.IndexKeys
                    .Descending(x => x.Timestamp)
                    .Descending(x => x.Id));

            var idIndex = new CreateIndexModel<LedgerEntry>(
                Builders<LedgerEntry>.IndexKeys.Ascending(x => x.Id));

            _collection.Indexes.CreateMany([lastIndex, idIndex]);
        }

        /// <inheritdoc />
        public Task WriteEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return _collection.InsertOneAsync(entry, cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetLastEntryAsync(CancellationToken cancellationToken = default)
        {
            LedgerEntry? last = await _collection
                .Find(Builders<LedgerEntry>.Filter.Empty)
                .SortByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return last;
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId, CancellationToken cancellationToken = default)
        {
            LedgerEntry? entry = await _collection
                .Find(e => e.Id == entryId)
                .FirstOrDefaultAsync(cancellationToken);

            return entry;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
        {
            return await _collection
                .Find(Builders<LedgerEntry>.Filter.Empty)
                .SortBy(e => e.Timestamp)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken);
        }
    }
}