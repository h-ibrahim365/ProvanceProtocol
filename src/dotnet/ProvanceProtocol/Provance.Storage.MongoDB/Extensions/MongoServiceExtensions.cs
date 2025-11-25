using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.MongoDB.Extensions
{
    /// <summary>
    /// Dependency injection extensions for registering the PROVANCE MongoDB storage provider.
    /// </summary>
    public static class MongoServiceExtensions
    {
        /// <summary>
        /// Registers the MongoDB-backed <see cref="ILedgerStore"/> implementation and configures
        /// MongoDB/BSON serialization rules required by PROVANCE.
        /// </summary>
        /// <remarks>
        /// This method:
        /// <list type="bullet">
        /// <item><description>Registers a BSON class map for <see cref="LedgerEntry"/> (maps <c>Id</c> to MongoDB <c>_id</c>).</description></item>
        /// <item><description>Serializes <see cref="DateTimeOffset"/> deterministically as a BSON string.</description></item>
        /// <item><description>Ensures <see cref="Guid"/> values are stored using the standard UUID representation.</description></item>
        /// <item><description>Binds <see cref="MongoDbOptions"/> from configuration section <c>"MongoDb"</c>.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="services">The DI service collection.</param>
        /// <param name="configuration">Application configuration (used to bind <see cref="MongoDbOptions"/>).</param>
        /// <returns>The same <see cref="IServiceCollection"/> instance for chaining.</returns>
        public static IServiceCollection AddProvanceMongoStorage(this IServiceCollection services, IConfiguration configuration)
        {
            // Map LedgerEntry.Id => MongoDB _id, and serialize Timestamp deterministically
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

            // Ensure GUIDs are stored in a standard, queryable format (UUID)
            // (If a serializer is already registered, keep it.)
            try
            {
                _ = BsonSerializer.SerializerRegistry.GetSerializer<Guid>();
            }
            catch (BsonSerializationException)
            {
                BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            }

            services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));
            services.AddSingleton<ILedgerStore, MongoLedgerStore>();

            return services;
        }
    }
}