# Provance.Storage.EntityFrameworkCore

Stores PROVANCE ledgers through Entity Framework Core, so any database with an EF Core provider works:
SQL Server, PostgreSQL, SQLite, MySQL, MariaDB, Oracle and others.

```bash
dotnet add package Provance.Storage.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # or the provider of your database
```

## Option 1: a table of its own (simplest)

```csharp
services
    .AddProvance(configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(configuration.GetConnectionString("Audit")));
```

The table `provance_ledger` and its unique index are created on first use. Change the name with
`o => o.TableName = "..."` as second argument.

## Option 2: inside your own DbContext and migrations

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddProvanceLedger();   // table name and schema are optional parameters
}
```

```csharp
services.AddDbContext<AppDbContext>(db => db.UseSqlServer(connectionString));
services
    .AddProvance(configuration.GetSection("Provance"))
    .UseEntityFrameworkCore<AppDbContext>();
```

Then add a migration as usual. Keep the unique index on `seq`: it is what keeps the chain linear when
several instances write at the same time.

## Tested databases

The test suite runs against SQLite, PostgreSQL and SQL Server. Other relational providers use the same
code path (standard LINQ, one `SaveChanges` per batch, no provider specific SQL).

### MongoDB

MongoDB works through the official `MongoDB.EntityFrameworkCore` provider, with option 2. The ledger logic is
the same as for the other databases; only the schema setup differs, because MongoDB has no tables and no
migrations:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddProvanceLedger();
    modelBuilder.Entity<LedgerEntryRecord>().ToCollection("provance_ledger");
}
```

```csharp
services.AddDbContext<AppDbContext>(db => db.UseMongoDB(connectionString, "audit"));
services
    .AddProvance(configuration.GetSection("Provance"))
    .UseEntityFrameworkCore<AppDbContext>(o => o.EnsureSchema = true);
```

With `EnsureSchema`, the store calls `EnsureCreated`, which creates the collection and the unique index on
`seq` declared by `AddProvanceLedger`. A replica set is recommended so that each batch is written in a
transaction. This combination is not covered by the test suite yet.

## Recommended database permissions

The application account needs `SELECT` and `INSERT` on the ledger table, plus `UPDATE` only if you use
payload redaction. Without `DELETE`, nobody can remove entries by mistake through the application.

Documentation: https://github.com/h-ibrahim365/ProvanceProtocol
