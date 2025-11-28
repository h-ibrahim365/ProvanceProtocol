using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.MongoDB
{
    /// <inheritdoc cref="ILedgerStore" />
    public class MongoLedgerStore : ILedgerStore
    {
        private readonly IMongoCollection<LedgerEntry> _collection;
        private readonly IMongoCollection<BsonDocument> _lockCollection;

        /// <inheritdoc cref="ILedgerStore" />
        public MongoLedgerStore(IOptions<MongoDbOptions> options)
        {
            var mongoClient = new MongoClient(options.Value.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(options.Value.DatabaseName);

            _collection = mongoDatabase.GetCollection<LedgerEntry>(options.Value.CollectionName);
            _lockCollection = mongoDatabase.GetCollection<BsonDocument>("provance_locks");

            // Index used to fetch the chain head quickly.
            var headIndex = new CreateIndexModel<LedgerEntry>(
                Builders<LedgerEntry>.IndexKeys
                    .Descending(x => x.Sequence)
                    .Descending(x => x.Id));

            // Lookup index by entry id.
            var idIndex = new CreateIndexModel<LedgerEntry>(
                Builders<LedgerEntry>.IndexKeys.Ascending(x => x.Id));

            // Strong ordering guarantee (optional but recommended).
            var sequenceUniqueIndex = new CreateIndexModel<LedgerEntry>(
                Builders<LedgerEntry>.IndexKeys.Ascending(x => x.Sequence),
                new CreateIndexOptions { Unique = true });

            _collection.Indexes.CreateMany([headIndex, idIndex, sequenceUniqueIndex]);
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
            return await _collection
                .Find(Builders<LedgerEntry>.Filter.Empty)
                .SortByDescending(e => e.Sequence)
                .ThenByDescending(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetEntryByIdAsync(Guid entryId, CancellationToken cancellationToken = default)
        {
            return await _collection
                .Find(e => e.Id == entryId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
        {
            return await _collection
                .Find(Builders<LedgerEntry>.Filter.Empty)
                .SortBy(e => e.Sequence)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<bool> AcquireOrRenewLeaseAsync(
            string resourceName,
            string workerId,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.Add(duration);

            // Renew if expired OR if we already hold it.
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", resourceName),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Lt("expiresAt", now.ToString("O")),
                    Builders<BsonDocument>.Filter.Eq("holder", workerId)
                )
            );

            var update = Builders<BsonDocument>.Update
                .Set("holder", workerId)
                .Set("expiresAt", expiresAt.ToString("O"))
                .Set("lastHeartbeat", now.ToString("O"));

            try
            {
                var updated = await _lockCollection.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument>
                    {
                        IsUpsert = false,
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken);

                if (updated != null)
                    return true;

                // If update failed, try atomic insert (will fail if already created).
                var insertDoc = new BsonDocument
                {
                    { "_id", resourceName },
                    { "holder", workerId },
                    { "expiresAt", expiresAt.ToString("O") },
                    { "lastHeartbeat", now.ToString("O") }
                };

                await _lockCollection.InsertOneAsync(insertDoc, new InsertOneOptions(), cancellationToken);
                return true;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Another worker holds a valid lease.
                return false;
            }
        }
    }
}