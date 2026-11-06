using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Provance.Core.Services.Interfaces;
using Provance.Storage.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registration of the Entity Framework Core store.</summary>
    public static class ProvanceEntityFrameworkBuilderExtensions
    {
        /// <summary>
        /// Stores the ledger in its own table through a built-in context. The table is created on first use.
        /// </summary>
        /// <example>
        /// <code>
        /// .UseEntityFrameworkCore(db => db.UseNpgsql(connectionString))
        /// .UseEntityFrameworkCore(db => db.UseSqlServer(connectionString))
        /// .UseEntityFrameworkCore(db => db.UseSqlite("Data Source=audit.db"))
        /// </code>
        /// </example>
        public static IProvanceBuilder UseEntityFrameworkCore(
            this IProvanceBuilder builder,
            Action<DbContextOptionsBuilder> configureDatabase,
            Action<EntityFrameworkStoreOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configureDatabase);

            builder.Services.AddDbContext<ProvanceLedgerDbContext>(db =>
            {
                configureDatabase(db);
                db.ReplaceService<IModelCacheKeyFactory, ProvanceModelCacheKeyFactory>();
            });

            var options = builder.Services.AddOptions<EntityFrameworkStoreOptions>().Configure(o => o.EnsureSchema = true);
            if (configure is not null)
            {
                options.Configure(configure);
            }

            return Register<ProvanceLedgerDbContext>(builder);
        }

        /// <summary>
        /// Stores the ledger through your own context. Call <c>modelBuilder.AddProvanceLedger()</c> in its
        /// <c>OnModelCreating</c> and create the table with your migrations.
        /// </summary>
        public static IProvanceBuilder UseEntityFrameworkCore<TContext>(
            this IProvanceBuilder builder,
            Action<EntityFrameworkStoreOptions>? configure = null)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);

            var options = builder.Services.AddOptions<EntityFrameworkStoreOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            return Register<TContext>(builder);
        }

        private static IProvanceBuilder Register<TContext>(IProvanceBuilder builder)
            where TContext : DbContext
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EntityFrameworkStoreOptions>, EntityFrameworkStoreOptionsValidator>());
            builder.Services.Replace(ServiceDescriptor.Singleton<ILedgerStore, EntityFrameworkLedgerStore<TContext>>());
            return builder;
        }
    }
}
