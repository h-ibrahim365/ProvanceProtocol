# .NET implementation

This document describes how the .NET packages implement [the ledger format](SPEC.md).

## Packages

| Package | Content |
| :------ | :------ |
| `Provance.Core` | Format (canonicalization, hashing, signing), writer, verifier, checkpoints, in-memory store, DI registration. Depends only on `Microsoft.Extensions.*.Abstractions`. |
| `Provance.Storage.EntityFrameworkCore` | `EntityFrameworkLedgerStore<TContext>`, one table per ledger, any EF Core provider. |
| `Provance.AspNetCore.Middleware` | Middleware that records one entry per HTTP request. |

## Write path

```
AddEntryAsync(eventType, payload)
  1. serialize payload with its runtime type, canonicalize (RFC 8785), hash it   (caller thread)
  2. put a PendingEntry with a fixed id into a bounded queue                   (caller thread)
  3. writer reads up to MaxBatchSize entries                                   (background)
  4. writer seals them on top of the head: sequence, previousHash, hash, signature
  5. store.AppendAsync(batch)
  6. callers are released with their sealed entry
```

The call returns once the entry is persisted. Canceling the caller's token stops the wait, not the write.

## Failures

The writer keeps the head (sequence and hash of the last entry) in memory. After any failed append it
forgets the head and does the following:

1. For each pending entry, in order, look it up by id. Entries found were persisted despite the error
   (lost acknowledgement, partial batch): their callers are released with the stored entry.
2. Reload the head from the store.
3. Reseal the remaining entries on top of the real head and try again.

Conflicts (the store reports that the sequence or id exists) are retried immediately, up to
`MaxConflictRetries`. Other errors are retried with exponential backoff, up to `MaxAppendAttempts`;
after that the callers receive `LedgerUnavailableException` and the writer continues with the next batch.

Because ids are fixed before the first attempt, an entry is never written twice: a retry of an entry that
was in fact persisted fails on the unique id, and the lookup of step 1 then finds it.

## Several application instances

Each instance runs its own writer. They share the store, and the store's uniqueness constraint on
`sequence` decides who wins each position. The loser reloads the head and reseals. No lease, no leader
election, no fencing token: an instance paused for a minute simply loses its next append and retries.

Throughput of a single ledger is bounded by the store's append latency, since appends are serialized by
the chain itself. Batching amortizes it under load. If you need more, split into several ledgers
(one per tenant or per domain).

## Shutdown

`StopAsync` stops accepting new entries and lets the writer drain the queue until the host shutdown
timeout. Entries that are still queued when the timeout expires fail with `LedgerUnavailableException`;
no caller is left waiting forever.

## Verification

`LedgerVerifier` streams entries from the store (`IAsyncEnumerable`), so memory use does not grow with
the ledger. It can start from genesis or, with `StartFromCheckpoint`, from a trusted checkpoint for
incremental checks. It can be used outside the application, with only a store and a signer that knows
the verification keys.

## Keys

`HmacLedgerSigner` uses `Provance:SecretKey` (current key, `Provance:SigningKeyId`) and
`Provance:RetiredKeys` (old keys, verification only). To rotate: move the current key to `RetiredKeys`,
set a new `SigningKeyId` and `SecretKey`. Keys shorter than 32 bytes and known placeholder values are
refused at startup.

To keep the key out of the application, implement `ILedgerSigner` on top of your KMS and register it
with `UseSigner<T>()`. The writer signs every entry, so a remote signer adds its latency to every append.

## Checkpoints

`CheckpointPublisher` runs only when at least one `ILedgerCheckpointSink` is registered. Every
`CheckpointInterval` it publishes a signed checkpoint of the head if the head moved. `FileCheckpointSink`
writes one file per checkpoint and never overwrites one; point it at storage the application cannot
rewrite. Store the latest checkpoint somewhere your auditors can read it and pass it to
`VerifyAsync(new LedgerVerificationOptions { TrustedCheckpoint = ... })`.

## Entity Framework Core store

* One table per ledger (`provance_ledger` by default), primary key on the entry id, unique index on `seq`.
* Either a built-in context that creates the table on first use (`UseEntityFrameworkCore(db => ...)`),
  or your own context with `modelBuilder.AddProvanceLedger()` and your migrations (`UseEntityFrameworkCore<TContext>()`).
* The payload is stored as the canonical JSON text, never as a JSON column: a JSON type would reorder
  members or change numbers and break hashes.
* A batch is one `SaveChanges`, so it is written completely or not at all.
* Conflicts are detected without provider specific error codes: after a failed insert, the store checks
  whether one of the batch's sequences or ids now exists. The same code therefore works on every relational
  provider.
* The ledger is read page by page (`ReadBatchSize`), with short queries, so verifying a large ledger does not
  hold a long transaction.
* Give the application account `SELECT` and `INSERT` on the table, plus `UPDATE` only for redaction, and `DELETE`
  only if you use retention.
* Named ledgers and archives use the built-in context with their own table (`provance_ledger_<name>`,
  `provance_archive`), created outside `AddDbContext`, so several can live in one process and one database.
* Retention deletes with `ExecuteDelete` on relational providers, page by page on the others.

## Several ledgers

Everything a ledger needs (options, store, signer, queue, writer, checkpoint publisher, archive copy, retention) is
built once per ledger name, the first time the ledger is resolved. The default ledger has an empty name and is
resolved as `ILedgerService`; named ledgers are keyed services (`[FromKeyedServices("billing")]`). Each ledger has
its own writer and its own chain, so ledgers never wait for each other.

## Archive copy

The archive copy is a background service per ledger. The writer wakes it after each append; it also runs every
`ArchiveInterval`. It reads the archive head, then copies primary entries from the next sequence, in batches of
`MaxBatchSize`, after checking that each entry links to the previous archived one and verifies on its own. The
archive's uniqueness constraint resolves races between instances, exactly as for the primary store. The copy
service stops after the writer on shutdown, so the last entries are copied.

`LedgerComparer` reads both copies in sequence order at the same time, so memory does not grow with the ledger.
Payloads are compared by flattening both JSON documents into paths (`payload.data.amount`, `payload.items[2]`).

## Retention

`ApplyRetentionAsync` verifies the primary store, chooses the last entry older than `KeepFor` as the new first
entry, checks the archive copy of everything before it, appends a `provance.retention` record (an ordinary signed
entry, see SPEC.md section 9), then deletes. The verifier accepts a ledger that starts after sequence 1 only with
such a record. Stores implement `IPrunableLedgerStore` to support it.

## Writing your own store

Implement `ILedgerStore`:

* reject an entry whose `Sequence` or `Id` already exists, with `LedgerConcurrencyException`;
* insert a batch in order (all or nothing is best, a prefix is accepted);
* keep `PayloadJson` byte for byte and timestamps with at least millisecond precision;
* return entries in ascending sequence from `ReadAsync`;
* for retention, also implement `IPrunableLedgerStore.DeleteBeforeAsync`.

## HTTP middleware

The middleware runs the rest of the pipeline, then writes one entry: method, path, status code, outcome,
duration, trace id, actor id and exception type. Query string, client IP and user agent are off by default
because they often contain personal data, and exception messages are never recorded. Health and metrics
paths are excluded.

If the ledger cannot be written, the default is to log and let the response go through
(`HttpAuditFailureMode.LogAndContinue`). `Throw` turns the failure into an error when the response has
not started. In both modes the wait is bounded by `AuditTimeout`, and an exception from the endpoint is
never replaced by an audit error.

Auditing after the endpoint means a request can succeed without an audit entry when the ledger is down.
For operations that must not happen unaudited, call `AddEntryAsync` in the endpoint before doing the work.
