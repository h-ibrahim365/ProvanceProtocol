namespace Provance.Storage.MongoDB
{
    public class MongoDbOptions
    {
        public const string SectionName = "MongoDb";

        public required string ConnectionString { get; set; }
        public required string DatabaseName { get; set; }
        public string CollectionName { get; set; } = "ledger_entries";
    }
}