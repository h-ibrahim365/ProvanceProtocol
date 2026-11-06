using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Provance.Storage.EntityFrameworkCore
{
    /// <summary>
    /// Built-in context containing only the ledger table. Used by <c>UseEntityFrameworkCore(db => db.UseXxx(...))</c>.
    /// </summary>
    public sealed class ProvanceLedgerDbContext : DbContext
    {
        private readonly EntityFrameworkStoreOptions _storeOptions;

        /// <summary>Creates the context (resolved by dependency injection).</summary>
        public ProvanceLedgerDbContext(DbContextOptions<ProvanceLedgerDbContext> options, IOptions<EntityFrameworkStoreOptions> storeOptions)
            : base(options)
        {
            _storeOptions = storeOptions.Value;
        }

        internal string TableName => _storeOptions.TableName;

        internal string? Schema => _storeOptions.Schema;

        /// <summary>Ledger rows.</summary>
        public DbSet<LedgerEntryRecord> Entries => Set<LedgerEntryRecord>();

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddProvanceLedger(_storeOptions.TableName, _storeOptions.Schema);
        }
    }

    /// <summary>Includes the table name in EF Core's model cache key, so several ledgers can coexist in one process.</summary>
    internal sealed class ProvanceModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is ProvanceLedgerDbContext ledger
                ? (context.GetType(), ledger.TableName, ledger.Schema, designTime)
                : (context.GetType(), designTime);
    }
}
