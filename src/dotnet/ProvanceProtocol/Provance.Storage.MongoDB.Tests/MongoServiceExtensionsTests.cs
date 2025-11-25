using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using Provance.Core.Data;
using Provance.Storage.MongoDB.Extensions;
using Xunit;

namespace Provance.Storage.MongoDB.Tests
{
    [Collection("BsonSerializerTests")]
    public class MongoServiceExtensionsTests
    {
        [Fact]
        public void AddProvanceMongoStorage_CalledTwice_ShouldNotThrow()
        {
            // Arrange
            var services = new ServiceCollection();

            var configData = new Dictionary<string, string>
            {
                {"MongoDb:ConnectionString", "mongodb://dummy"},
                {"MongoDb:DatabaseName", "test_db"},
                {"MongoDb:CollectionName", "test_collection"}
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData!)
                .Build();

            // Act
            services.AddProvanceMongoStorage(configuration);

            var exception = Record.Exception(() =>
            {
                services.AddProvanceMongoStorage(configuration);
            });

            // Assert
            Assert.Null(exception);

            Assert.True(BsonClassMap.IsClassMapRegistered(typeof(LedgerEntry)));
        }
    }
}