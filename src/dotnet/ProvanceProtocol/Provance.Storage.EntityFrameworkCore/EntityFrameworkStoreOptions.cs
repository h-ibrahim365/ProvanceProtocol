using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Provance.Storage.EntityFrameworkCore
{
    /// <summary>Options of the Entity Framework Core store.</summary>
    public sealed class EntityFrameworkStoreOptions
    {
        /// <summary>
        /// Table name used by the built-in <see cref="ProvanceLedgerDbContext"/>. When you use your own context,
        /// pass the name to <c>AddProvanceLedger(...)</c> instead. Default <c>provance_ledger</c>.
        /// </summary>
        public string TableName { get; set; } = ProvanceModelBuilderExtensions.DefaultTableName;

        /// <summary>Optional schema of the built-in context's table.</summary>
        public string? Schema { get; set; }

        /// <summary>
        /// Create the database and the ledger table on first use when they do not exist. On relational providers
        /// only the ledger table is created; on MongoDB, <c>EnsureCreated</c> creates the collections and indexes of
        /// the model. <c>true</c> by default with the built-in context, <c>false</c> with your own context, whose
        /// schema is managed by your migrations.
        /// </summary>
        public bool EnsureSchema { get; set; }

        /// <summary>Number of rows fetched per round trip while streaming the ledger. Default 1000.</summary>
        public int ReadBatchSize { get; set; } = 1000;
    }

    internal sealed class EntityFrameworkStoreOptionsValidator : IValidateOptions<EntityFrameworkStoreOptions>
    {
        public ValidateOptionsResult Validate(string? name, EntityFrameworkStoreOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.TableName))
                return ValidateOptionsResult.Fail("PROVANCE EF Core: TableName is required.");
            if (options.ReadBatchSize < 1)
                return ValidateOptionsResult.Fail("PROVANCE EF Core: ReadBatchSize must be at least 1.");
            return ValidateOptionsResult.Success;
        }
    }
}
