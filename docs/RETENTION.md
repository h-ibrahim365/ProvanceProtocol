# Retention (.NET 0.2)

A ledger is append only, so it grows forever. Retention keeps the primary store small (for example the last
7 days) while the archive keeps everything, and both stay verifiable.

## Setting it up

```json
{
  "Provance": {
    "LedgerId": "orders-api-prod",
    "Retention": { "KeepFor": "7.00:00:00", "Interval": "1.00:00:00" }
  }
}
```

With an archive configured (see [ARCHIVE.md](ARCHIVE.md)), entries older than `KeepFor` leave the primary store
once a day, and about a minute after each start. Or run it yourself, for example from a scheduled job:

```csharp
LedgerRetentionResult result = await ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));
```

## What one run does

1. Verifies the primary store. If it does not verify, nothing is removed: the evidence stays where it is.
2. Picks the boundary: the last entry older than `KeepFor`. It stays, as the first entry of the primary store.
3. Copies the latest entries to the archive and checks that the archive holds an identical copy of every entry
   before the boundary. Otherwise nothing is removed.
4. Appends a retention record (`provance.retention`), signed like any entry, naming the boundary entry and its
   hash.
5. Deletes every entry before the boundary.
6. Creates a checkpoint of the head and publishes it to the checkpoint sinks.

Without an archive, nothing is removed, unless you set `Retention:RequireArchive` to `false` because you keep a
copy another way (a daily export, for example).

## Verifying afterwards

`VerifyAsync()` works as before. The primary store now starts at the boundary, and the verifier accepts that only
because the retention record vouches for it (see [SPEC.md](SPEC.md), section 9). Deleting the first entries without
the signing key is still detected:

```
Entries 1 to 1399 are missing, and no retention record in the ledger accounts for their removal.
```

The archive keeps the whole chain from the first entry and verifies like any ledger. A trusted checkpoint must
reference an entry still in the primary store: use the latest one (step 6 publishes one after each run). An older
checkpoint is reported as removed by retention rather than silently ignored.

## Permissions

Retention needs `DELETE` on the primary table. The archive account still needs no `DELETE` at all: what is removed
from the primary store is exactly what the archive keeps.

## Why not one ledger per period

Starting a new ledger every week (a new ledger id, a new table) would also bound the size, but the chain would be
cut at every period, each period would need its own table, and an auditor would have to check how periods link.
With retention there is a single chain, in a single table, and the archive holds it whole.
