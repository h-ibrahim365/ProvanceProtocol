using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.EntityFrameworkCore
{
    /// <summary>
    /// <see cref="ILedgerStore"/> on top of any Entity Framework Core provider.
    /// </summary>
    /// <remarks>
    /// <para>Conflicts are detected without parsing provider specific error codes: when an insert fails, the store
    /// checks whether a row with one of the batch's sequences or ids now exists. This works the same on SQL Server,
    /// PostgreSQL, SQLite, MySQL, Oracle and others, as long as the unique index on the sequence exists.</para>
    /// <para>A batch is saved in a single <c>SaveChanges</c>, so it is persisted completely or not at all on
    /// providers with transactions.</para>
    /// </remarks>
    /// <typeparam name="TContext">A context whose model contains the ledger table (see <c>AddProvanceLedger</c>).</typeparam>
    public sealed class EntityFrameworkLedgerStore<TContext> : ILedgerStore
        where TContext : DbContext
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly EntityFrameworkStoreOptions _options;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private volatile bool _initialized;

        /// <summary>Creates the store (resolved by dependency injection).</summary>
        public EntityFrameworkLedgerStore(IServiceScopeFactory scopes, IOptions<EntityFrameworkStoreOptions> options)
        {
            _scopes = scopes;
            _options = options.Value;
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default)
        {
            await using var lease = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var record = await lease.Entries.AsNoTracking()
                .OrderByDescending(r => r.Sequence)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return record?.ToEntry();
        }

        /// <inheritdoc />
        public async Task AppendAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);
            if (entries.Count == 0)
            {
                return;
            }

            try
            {
                await using var lease = await OpenAsync(cancellationToken).ConfigureAwait(false);
                lease.Entries.AddRange(entries.Select(LedgerEntryRecord.From));
                await lease.Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (await ConflictExistsAsync(entries, cancellationToken).ConfigureAwait(false))
                {
                    throw new LedgerConcurrencyException("An entry with the same sequence or id already exists.", ex);
                }
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<LedgerEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var lease = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var record = await lease.Entries.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                .ConfigureAwait(false);
            return record?.ToEntry();
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LedgerEntry> ReadAsync(long fromSequence = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Paged by sequence rather than one long-running query: keeps transactions and connections short on every provider.
            long next = fromSequence;
            while (true)
            {
                List<LedgerEntryRecord> page;
                await using (var lease = await OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    long from = next;
                    page = await lease.Entries.AsNoTracking()
                        .Where(r => r.Sequence >= from)
                        .OrderBy(r => r.Sequence)
                        .Take(_options.ReadBatchSize)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var record in page)
                {
                    yield return record.ToEntry();
                }

                if (page.Count < _options.ReadBatchSize)
                {
                    yield break;
                }
                next = page[^1].Sequence + 1;
            }
        }

        /// <inheritdoc />
        public async Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var lease = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var record = await lease.Entries.FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return false;
            }

            record.PayloadJson = null;
            record.RedactedAt = DateTimeOffset.UtcNow;
            await lease.Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> ConflictExistsAsync(IReadOnlyList<LedgerEntry> entries, CancellationToken ct)
        {
            try
            {
                await using var lease = await OpenAsync(ct).ConfigureAwait(false);
                var set = lease.Entries.AsNoTracking();

                // Batches have contiguous sequences, so a range query covers all of them.
                long first = entries[0].Sequence;
                long last = entries[^1].Sequence;
                if (await set.AnyAsync(r => r.Sequence >= first && r.Sequence <= last, ct).ConfigureAwait(false))
                {
                    return true;
                }

                // An id can exist at another sequence when an earlier attempt was persisted.
                foreach (var entry in entries)
                {
                    Guid id = entry.Id;
                    if (await set.AnyAsync(r => r.Id == id, ct).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false; // cannot tell: report the original failure, the writer will retry
            }
        }

        private async Task<ContextLease> OpenAsync(CancellationToken ct)
        {
            if (!_initialized)
            {
                await InitializeAsync(ct).ConfigureAwait(false);
            }
            return ContextLease.Create(_scopes);
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                if (_options.EnsureSchema)
                {
                    await using var lease = ContextLease.Create(_scopes);
                    await EnsureSchemaAsync(lease.Context, ct).ConfigureAwait(false);
                }
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private static async Task EnsureSchemaAsync(DbContext context, CancellationToken ct)
        {
            if (!context.Database.IsRelational())
            {
                // MongoDB provider: creates the collections and the indexes declared in the model, unique sequence included.
                await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
                return;
            }

            var creator = context.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await creator.CreateAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Accept the failure only if another instance created the database concurrently.
                    if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
                    {
                        throw;
                    }
                }
            }

            if (await TableExistsAsync(context, ct).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await creator.CreateTablesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Accept the failure only if another instance created the table concurrently.
                if (!await TableExistsAsync(context, ct).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        private static async Task<bool> TableExistsAsync(DbContext context, CancellationToken ct)
        {
            try
            {
                _ = await context.Set<LedgerEntryRecord>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>A context from a fresh DI scope, or from <see cref="IDbContextFactory{TContext}"/> when one is registered.</summary>
        private sealed class ContextLease : IAsyncDisposable
        {
            private readonly AsyncServiceScope _scope;
            private readonly bool _ownsContext;

            private ContextLease(AsyncServiceScope scope, TContext context, bool ownsContext)
            {
                _scope = scope;
                Context = context;
                _ownsContext = ownsContext;
            }

            public TContext Context { get; }

            public DbSet<LedgerEntryRecord> Entries => Context.Set<LedgerEntryRecord>();

            public static ContextLease Create(IServiceScopeFactory scopes)
            {
                var scope = scopes.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetService<IDbContextFactory<TContext>>();
                return factory is not null
                    ? new ContextLease(scope, factory.CreateDbContext(), ownsContext: true)
                    : new ContextLease(scope, scope.ServiceProvider.GetRequiredService<TContext>(), ownsContext: false);
            }

            public async ValueTask DisposeAsync()
            {
                if (_ownsContext)
                {
                    await Context.DisposeAsync().ConfigureAwait(false);
                }
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
