using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Provance.Storage.EntityFrameworkCore.Tests
{
    /// <summary>SQLite file database. Runs everywhere, no Docker needed.</summary>
    public sealed class SqliteStoreTests : StoreContractTests, IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"provance-{Guid.NewGuid():N}.db");

        protected override void ConfigureDatabase(DbContextOptionsBuilder db) => db.UseSqlite($"Data Source={_path};Pooling=False");

        protected override int ConcurrentWritesPerInstance => 20;

        public void Dispose()
        {
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    /// <summary>PostgreSQL in a container (Docker required).</summary>
    public sealed class PostgreSqlStoreTests : StoreContractTests, IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

        protected override void ConfigureDatabase(DbContextOptionsBuilder db) => db.UseNpgsql(_container.GetConnectionString());

        public Task InitializeAsync() => _container.StartAsync();

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }

    /// <summary>SQL Server in a container (Docker required).</summary>
    public sealed class SqlServerStoreTests : StoreContractTests, IAsyncLifetime
    {
        private readonly MsSqlContainer _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();

        protected override void ConfigureDatabase(DbContextOptionsBuilder db) => db.UseSqlServer(_container.GetConnectionString());

        public Task InitializeAsync() => _container.StartAsync();

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }
}
