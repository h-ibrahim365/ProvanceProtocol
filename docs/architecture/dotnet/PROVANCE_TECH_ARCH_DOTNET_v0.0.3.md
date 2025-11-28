<p align="center">
  <img src="/icons/provanceProtocol.png" alt="PROVANCE Protocol logo" width="140" />
</p>

# PROVANCE Technical Architecture (.NET) — v0.0.3

This document describes the **v0.0.3** architecture, focusing on the **Single Writer** model, deterministic ordering (`Sequence`), hashing, and storage behavior.

> v0.0.3 is pre-release. The goal of this version is **correctness under concurrency** (anti-fork).

---

## 1) High-level design

PROVANCE v0.0.3 is built around a strict separation:

- **Producers (API calls)** create *intent* and enqueue it quickly.
- A **Single Writer background service** is the only component that:
  - assigns the next `Sequence`
  - reads/maintains the chain head (`PreviousHash`)
  - computes the HMAC (`CurrentHash`)
  - persists the entry to the store

This prevents forks and makes verification deterministic.

---

## 2) Core components

### 2.1 `LedgerService` (Producer)
Responsibilities:
- Validate input (`eventType`, `payload`)
- Build a `LedgerTransactionContext` containing intent + a `TaskCompletionSource<LedgerEntry>`
- Enqueue the intent into `IEntryQueue`
- Await the acknowledgment (v0.0.3 waits until persistence succeeds)

Non-responsibilities:
- Does **not** compute hashes
- Does **not** read chain head
- Does **not** assign ordering

### 2.2 `IEntryQueue` (Bounded async channel)
Responsibilities:
- Provide backpressure (bounded capacity)
- Preserve FIFO semantics for the Single Writer
- Provide async waiting (no thread blocking)

### 2.3 `LedgerWriterService` (Single Writer, Consumer)
Responsibilities:
- Acquire an exclusive lease (anti-fork safety)
- Initialize local chain state:
  - `localPreviousHash = lastEntry.CurrentHash` or `GenesisHash`
  - `localSequence = lastEntry.Sequence` or `0`
- Drain the queue and seal entries strictly in order
- Persist entries and acknowledge producers

Lease behavior:
- Uses a periodic heartbeat to renew the lease
- If the lease is lost, the service **fails fast** (crashes) to prevent forks

### 2.4 `ILedgerStore`
Responsibilities:
- Persist sealed entries (`WriteEntryAsync`)
- Provide chain head (`GetLastEntryAsync`) ordered by `Sequence desc`
- Provide full ordered chain (`GetAllEntriesAsync`) ordered by `Sequence asc`
- Provide distributed lease primitive (`AcquireOrRenewLeaseAsync`)

---

## 3) Data model changes in v0.0.3

### 3.1 Deterministic ordering with `Sequence`
Why:
- Timestamps can collide under load and do not provide a strict ordering guarantee.
- `Sequence` is assigned by the Single Writer and is **monotonic**.

Rules:
- `Sequence` starts at 1 for the first entry of a ledger
- It increments by 1 for each appended entry
- It is part of the signed content so reordering is tamper-evident

### 3.2 Hashing
`CurrentHash` is HMAC-SHA256 over a canonical JSON structure that includes:
- `sequence`
- `id`
- `timestamp`
- `previousHash` (normalized to lower-case)
- `eventType`
- `payload`

`CurrentHash` is excluded from the signed content.

---

## 4) Write path (end-to-end)

### 4.1 Sequence diagram (PlantUML)

```plantuml
@startuml
title PROVANCE v0.0.3 — Write Path (Single Writer)

actor Client
participant "ASP.NET Core" as ASP
participant "LedgerService (Producer)" as Producer
participant "IEntryQueue (Channel)" as Q
participant "LedgerWriterService (Single Writer)" as Writer
database "ILedgerStore (MongoDB)" as Store

Client -> ASP: HTTP request
ASP -> Producer: AddEntryAsync(eventType, payload)
Producer -> Q: EnqueueAsync(context)
(intent + AckSource)
Producer -> Producer: await AckSource.Task

Writer -> Store: AcquireOrRenewLeaseAsync(lock)
(heartbeat renews)
Writer -> Store: GetLastEntryAsync()
(init localSequence, localPreviousHash)

loop for each context
  Writer -> Writer: nextSequence = localSequence + 1
  Writer -> Writer: build entry
(Sequence, PreviousHash)
  Writer -> Writer: HMAC(CurrentHash)
  Writer -> Store: WriteEntryAsync(entry)
  Writer -> Writer: localSequence++, localPreviousHash = CurrentHash
  Writer -> Producer: AckSource.SetResult(entry)
end

Producer --> ASP: return sealed entry
ASP --> Client: 200 OK (Id, Sequence, CurrentHash)
@enduml
```

### 4.2 Acknowledgement semantics (v0.0.3)
In v0.0.3, **ack happens after persistence** in the ledger store.

Pros:
- Strong correctness: successful return means the entry exists in the ledger.

Cons:
- Higher latency under load (because persistence is on the critical path of the request completion).

---

## 5) Verification path

Verification is read-only and safe to run concurrently:

1) Load all entries from store
2) Ensure ordering by `Sequence asc, Id asc`
3) For each entry:
   - Check chain continuity (`PreviousHash` matches expected)
   - Recompute HMAC and compare to stored `CurrentHash`

If any mismatch occurs, verification returns failure with the index/entry id and a reason.

---

## 6) MongoDB specifics (v0.0.3)

### 6.1 Collections
- Ledger entries: `MongoDb:CollectionName` (recommended: `ledger_entries_v1`)
- Locks: `provance_locks`

### 6.2 Indexes
Mongo store creates indexes on startup:
- `(Sequence desc, Id desc)` for chain head lookup
- `(Id asc)` for id lookup
- `Sequence unique` to enforce deterministic ordering

### 6.3 Lease document
A single document per resource is stored in `provance_locks`:
- `_id`: resource name (e.g. `ledger_writer_lock_v1`)
- `holder`: writer instance id
- `expiresAt`: ISO timestamp
- `lastHeartbeat`: ISO timestamp

---

## 7) Failure modes & operational notes

### 7.1 Crash before consumption
Because v0.0.3 does not have a durable outbox yet:
- any intent enqueued but not yet consumed by the writer can be lost on process crash.

Planned fix:
- v0.0.4 introduces a durable outbox + replay on startup.

### 7.2 Lost writer lease
If heartbeat cannot renew the lease:
- Writer must stop / crash to prevent forks.
- Orchestrators should restart the service.

### 7.3 Multi-instance deployments
v0.0.3 is **single-writer / single-instance oriented**.
Multiple app instances may exist, but only one writer should be active per ledger.

---

## 8) Roadmap alignment
Future versions will focus on:
- durable outbox + replay + checkpoints (v0.0.4)
- canonicalization test vectors for cross-language conformance (v0.0.5)
- observability (v0.0.6)

---

## License
MIT
