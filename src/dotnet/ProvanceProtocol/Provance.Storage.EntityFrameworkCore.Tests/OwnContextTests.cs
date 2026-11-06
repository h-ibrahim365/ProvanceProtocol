using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provance.Core.Data;
using Provance.Core.Services.Interfaces;

namespace Provance.Storage.EntityFrameworkCore.Tests
{
    /// <summary>The ledger table inside the application's own DbContext (option 2 of the README).</summary>
    public sealed class OwnContextTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"provance-app-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public async Task Ledger_lives_next_to_application_tables()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(db => db.UseSqlite($"Data Source={_path};Pooling=False"));
            services.AddProvance(o =>
            {
                o.LedgerId = "app";
                o.SecretKey = "own-context-test-secret-key-0123456789abcdef";
            }).UseEntityFrameworkCore<AppDbContext>();

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                // Stands in for "dotnet ef database update".
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            }

            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);

            var ledger = provider.GetRequiredService<ILedgerService>();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Orders.Add(new Order { Id = 1, Customer = "c-42" });
                await db.SaveChangesAsync();
            }
            await ledger.AddEntryAsync("ORDER_CREATED", new AuditedPayload { ActorId = "c-42" });

            var result = await ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(1, await db.Orders.CountAsync());
                Assert.Equal(1, await db.Set<LedgerEntryRecord>().CountAsync());
            }

            foreach (var service in hosted) await service.StopAsync(CancellationToken.None);
        }

        public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
        {
            public DbSet<Order> Orders => Set<Order>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>().HasKey(o => o.Id);
                modelBuilder.AddProvanceLedger("audit_ledger");
            }
        }

        public sealed class Order
        {
            public int Id { get; set; }
            public string Customer { get; set; } = string.Empty;
        }
    }
}
