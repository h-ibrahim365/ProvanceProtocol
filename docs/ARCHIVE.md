# Archive copy and field-level comparison (0.2)

Verification tells you that entry 42 was changed. It cannot tell you what it said before, because the ledger only
keeps a hash of it. An archive copy fixes that: every entry is also written to a second store, and comparing the
two copies shows, field by field, the original value and the current one.

Try it in the [demo](https://provance.iamhakim.com/#try-archive): change a row in either copy, then compare.

## Setting it up

```csharp
builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))
    .UseEntityFrameworkCore(db => db.UseNpgsql(appConnectionString))                 // primary store
    .UseEntityFrameworkCoreArchive(db => db.UseNpgsql(archiveConnectionString));     // archive, table provance_archive
```

Any `ILedgerStore` works as an archive: `UseArchive(store)`.

Java:

```java
ProvanceLedger ledger = ProvanceLedger.builder()
        .ledgerId("orders-api-prod")
        .secretKey(System.getenv("PROVANCE_SECRET_KEY"))
        .store(new JdbcLedgerStore(appDataSource))
        .archive(new JdbcLedgerStore(archiveDataSource, archiveTable))   // table provance_archive
        .build();
```

With Spring Boot, declare a `ProvanceArchive` bean wrapping the archive store, or set `provance.archive.enabled=true`
for a table `provance_archive` on the application `DataSource`.

Python:

```python
ledger = ProvanceLedger(ledger_id="orders-api-prod", secret_key=os.environ["PROVANCE_SECRET_KEY"],
                        store=SqlLedgerStore.from_url(app_url),
                        archive=SqlLedgerStore.from_url(archive_url, table="provance_archive"))
```

The names below are the .NET ones. Java uses the same names in camel case (`compareWithArchive()`,
`archiveStatus()`, `DifferenceKind.CHANGED`), Python in snake case (`compare_with_archive()`, `archive_status()`,
`DifferenceKind.CHANGED`).

The copy runs in the background, after each write and every `ArchiveInterval` (5 seconds by default). It never slows
down `AddEntryAsync`. If the archive is unreachable, entries wait in the primary store and are copied when it comes
back; on restart, copying resumes from the last archived entry. On shutdown, the last entries are copied after the
writer has drained its queue.

Before copying an entry, the library checks that it continues the archive's chain and that it verifies on its own
(payload hash, hash, signature). An entry modified in the primary store before it was copied is therefore not
propagated: copying stops at that entry, a critical log line is written, and `GetArchiveStatus()` reports the
problem.

## Where to put the archive

The archive is only useful if the people who can modify the primary store cannot also modify the archive:

* another database, or at least another schema, with its own credentials;
* the application account can `INSERT` and `SELECT` there, plus `UPDATE` of the `payload` column for erasure
  requests, and nothing else: no `DELETE`, no other `UPDATE`;
* ideally owned by another team (security, compliance), who also hold the checkpoints.

## Comparing the two copies

```csharp
LedgerComparison comparison = await ledger.CompareWithArchiveAsync();
if (!comparison.IsIdentical)
{
    logger.LogCritical("{Report}", comparison.Describe());
}
```

```
1 difference(s) in 1240 entries compared.
Changed fields are shown as: archive value -> primary value.

Entry 42 (ORDER_REFUNDED) was changed in the primary store (the archive copy verifies, the primary copy does not).
  payload.data.amount: "49.90" -> "4990.00"
```

Each `EntryDifference` has a `Kind`:

| Kind | Meaning |
| :--- | :------ |
| `Changed` | Both copies have the entry, with different content. `Fields` lists every field that differs (envelope fields such as `eventType` or `timestamp`, and payload paths such as `payload.data.amount`). |
| `MissingFromPrimary` | The archive has it, the primary store does not: deleted, or cut from the end. |
| `MissingFromArchive` | The primary store has it, the archive does not, although the archive already holds later entries. |
| `RedactedInOneCopy` | The payload was erased in one copy only. Not tampering. |

For a changed entry, `Original` says which copy is the original, by checking each copy on its own:

* `Archive`: the archive copy verifies, the primary copy does not. The primary store was modified.
* `Primary`: the opposite. The archive was modified.
* `BothVerify`: both copies carry valid signatures. Whoever changed one of them holds the signing key; the trusted
  checkpoint tells which copy is the real history.
* `NeitherVerifies`: both were modified.

Entries the primary store has not copied yet are counted in `NotYetCopied`, not reported as differences. Entries
removed from the primary store by retention are counted in `RemovedByRetention`.

## Comparing with an export

The comparison works between any two stores, including an export file:

```csharp
var export = await LedgerJsonLines.OpenAsync("audit-2026-10.jsonl");
var comparer = new LedgerComparer(signer, "orders-api-prod");
var comparison = await comparer.CompareAsync(primaryStore, export);
```

`LedgerJsonLines.WriteAsync(store, stream)` writes an export in the format shared by the three libraries (Java:
`LedgerJsonLines.write(store, writer)`; the Python `provance export` command writes the same).

From the command line (Python package), between two databases or two exports:

```bash
provance diff postgresql://auditor@db/app postgresql://auditor@archive/audit --archive-table provance_archive --ledger-id orders-api-prod
provance diff primary.jsonl archive.jsonl --ledger-id orders-api-prod --json
```

The exit code is 0 when the copies are identical, 1 otherwise.

## Erasure requests

`RedactPayloadAsync` erases the payload in the primary store and in the archive. If the archive refuses (for
example, no `UPDATE` permission), the primary store is erased anyway and `LedgerArchiveException` is thrown, so that
the request is not considered done while a copy of the personal data remains.
