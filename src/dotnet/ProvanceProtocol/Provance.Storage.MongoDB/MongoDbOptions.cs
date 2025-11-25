namespace Provance.Storage.MongoDB
{
    /// <summary>
    /// Configuration options for the PROVANCE MongoDB storage provider.
    /// </summary>
    public class MongoDbOptions
    {
        /// <summary>
        /// The name of the configuration section that binds to <see cref="MongoDbOptions"/>.
        /// Default: <c>"MongoDb"</c>.
        /// </summary>
        public const string SectionName = "MongoDb";

        /// <summary>
        /// The MongoDB connection string (e.g., <c>mongodb://localhost:27017</c>).
        /// In production, prefer injecting this via environment variables or a secret manager.
        /// </summary>
        public required string ConnectionString { get; set; }

        /// <summary>
        /// The MongoDB database name used to store PROVANCE ledger entries.
        /// </summary>
        public required string DatabaseName { get; set; }

        /// <summary>
        /// The MongoDB collection name used to store ledger documents.
        /// Default: <c>"ledger_entries"</c>.
        /// </summary>
        public string CollectionName { get; set; } = "ledger_entries";
    }
}