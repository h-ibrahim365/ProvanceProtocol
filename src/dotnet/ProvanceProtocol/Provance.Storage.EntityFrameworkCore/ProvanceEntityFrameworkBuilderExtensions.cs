using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Provance.Core.Services.Interfaces;
using Provance.Storage.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registration of the Entity Framework Core store.</summary>
    public static class ProvanceEntityFrameworkBuilderExtensions
    {
        /// <summary>Default table of an archive (<see cref="UseEntityFrameworkCoreArchive(IProvanceBuilder, Action{DbContextOptionsBuilder}, Action{EntityFrameworkStoreOptions}?)"/>).</summary>
        public const string DefaultArchiveTableName = "provance_archive";

        /// <summary>
        /// Stores the ledger in its own table through a built-in context. The table is created on first use.
        /// </summary>
        /// <remarks>
        /// For a named ledger (<c>AddProvance("billing", ...)</c>) the default table is <c>provance_ledger_billing</c>.
        /// </remarks>
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

            if (builder.LedgerName.Length > 0)
            {
                var named = StoreOptions(ProvanceModelBuilderExtensions.DefaultTableName + "_" + TableSuffix(builder.LedgerName), configure);
                return builder.UseStore(sp => CreateBuiltInStore(sp, configureDatabase, named));
            }

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
        /// <remarks>A context maps one ledger table: give each named ledger its own context type.</remarks>
        public static IProvanceBuilder UseEntityFrameworkCore<TContext>(
            this IProvanceBuilder builder,
            Action<EntityFrameworkStoreOptions>? configure = null)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (builder.LedgerName.Length > 0)
            {
                var named = StoreOptions(ProvanceModelBuilderExtensions.DefaultTableName, configure, ensureSchema: false);
                return builder.UseStore(sp => new EntityFrameworkLedgerStore<TContext>(sp.GetRequiredService<IServiceScopeFactory>(), named, null));
            }

            var options = builder.Services.AddOptions<EntityFrameworkStoreOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            return Register<TContext>(builder);
        }

        /// <summary>
        /// Copies every entry to an archive table through a built-in context, usually in another database or under an
        /// account that can only insert. The table is created on first use. Default table: <c>provance_archive</c>
        /// (<c>provance_archive_billing</c> for a named ledger).
        /// </summary>
        /// <example>
        /// <code>
        /// .UseEntityFrameworkCore(db => db.UseNpgsql(appConnectionString))
        /// .UseEntityFrameworkCoreArchive(db => db.UseNpgsql(archiveConnectionString))
        /// </code>
        /// </example>
        public static IProvanceBuilder UseEntityFrameworkCoreArchive(
            this IProvanceBuilder builder,
            Action<DbContextOptionsBuilder> configureDatabase,
            Action<EntityFrameworkStoreOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configureDatabase);

            string table = builder.LedgerName.Length == 0 ? DefaultArchiveTableName : DefaultArchiveTableName + "_" + TableSuffix(builder.LedgerName);
            var options = StoreOptions(table, configure);
            return builder.UseArchive(sp => CreateBuiltInStore(sp, configureDatabase, options));
        }

        /// <summary>
        /// Copies every entry to an archive table mapped in your own context (a different context type from the one of
        /// the primary store). Create the table with your migrations.
        /// </summary>
        public static IProvanceBuilder UseEntityFrameworkCoreArchive<TContext>(
            this IProvanceBuilder builder,
            Action<EntityFrameworkStoreOptions>? configure = null)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);
            var options = StoreOptions(DefaultArchiveTableName, configure, ensureSchema: false);
            return builder.UseArchive(sp => new EntityFrameworkLedgerStore<TContext>(sp.GetRequiredService<IServiceScopeFactory>(), options, null));
        }

        private static IProvanceBuilder Register<TContext>(IProvanceBuilder builder)
            where TContext : DbContext
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EntityFrameworkStoreOptions>, EntityFrameworkStoreOptionsValidator>());
            builder.Services.Replace(ServiceDescriptor.Singleton<ILedgerStore, EntityFrameworkLedgerStore<TContext>>());
            return builder;
        }

        /// <summary>Options of a store that is not configured through <c>IOptions</c> (named ledgers and archives).</summary>
        private static EntityFrameworkStoreOptions StoreOptions(string defaultTable, Action<EntityFrameworkStoreOptions>? configure, bool ensureSchema = true)
        {
            var options = new EntityFrameworkStoreOptions { TableName = defaultTable, EnsureSchema = ensureSchema };
            configure?.Invoke(options);
            var validation = new EntityFrameworkStoreOptionsValidator().Validate(null, options);
            if (validation.Failed)
            {
                throw new ArgumentException(validation.FailureMessage, nameof(configure));
            }
            return options;
        }

        /// <summary>A store with the built-in context, created outside <c>AddDbContext</c> so that several can coexist.</summary>
        private static EntityFrameworkLedgerStore<ProvanceLedgerDbContext> CreateBuiltInStore(
            IServiceProvider services, Action<DbContextOptionsBuilder> configureDatabase, EntityFrameworkStoreOptions options)
        {
            var db = new DbContextOptionsBuilder<ProvanceLedgerDbContext>();
            configureDatabase(db);
            db.ReplaceService<IModelCacheKeyFactory, ProvanceModelCacheKeyFactory>();
            var loggers = services.GetService<ILoggerFactory>();
            if (loggers is not null)
            {
                db.UseLoggerFactory(loggers);
            }

            var contextOptions = db.Options;
            var storeOptions = new OptionsWrapper<EntityFrameworkStoreOptions>(options);
            return new EntityFrameworkLedgerStore<ProvanceLedgerDbContext>(
                services.GetRequiredService<IServiceScopeFactory>(), options, () => new ProvanceLedgerDbContext(contextOptions, storeOptions));
        }

        /// <summary>Letters, digits and underscores of a ledger name, lowercased, for a default table name.</summary>
        private static string TableSuffix(string ledgerName)
        {
            var suffix = new StringBuilder(ledgerName.Length);
            foreach (char c in ledgerName)
            {
                suffix.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            }
            return suffix.ToString();
        }
    }
}
