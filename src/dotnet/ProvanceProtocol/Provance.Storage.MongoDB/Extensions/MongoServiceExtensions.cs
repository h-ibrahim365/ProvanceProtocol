using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Provance.Core.Data; // References Core
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.MongoDB.Extensions
{
    public static class MongoServiceExtensions
    {
        public static IServiceCollection AddProvanceMongoStorage(this IServiceCollection services, IConfiguration configuration)
        {
            // This tells Mongo: "When you see LedgerEntry, use the Id property as the primary key (_id)"
            BsonClassMap.RegisterClassMap<LedgerEntry>(cm =>
            {
                cm.AutoMap(); // Map all other properties automatically
                cm.MapIdMember(c => c.Id); // Explicitly map Id to _id
            });

            // Ensure GUIDs are stored in a standard, queryable format (UUID)
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));
            services.AddSingleton<ILedgerStore, MongoLedgerStore>();

            return services;
        }
    }
}