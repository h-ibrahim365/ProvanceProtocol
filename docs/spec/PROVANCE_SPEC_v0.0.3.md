# PROVANCE Protocol – Specification v0.0.3

> **Status**: Pre-release (v0.x) – breaking changes are still allowed.  
> **Scope of this spec**: .NET implementation (`Provance.Core`, `Provance.Storage.MongoDB`, `Provance.AspNetCore.Middleware`) at **v0.0.3**.

---

## 1. Goals & Non-goals

### 1.1 Goals

PROVANCE provides a **tamper-evident audit log**:

- Each entry is linked to the previous one via a cryptographic hash (`PreviousHash` / `CurrentHash`).
- Any modification, reordering, or insertion that breaks the chain is detectable.
- Writers run through a **single-writer pipeline** to avoid forks under concurrency.
- The protocol is **library-first** (no network/consensus layer).

### 1.2 Non-goals

PROVANCE does **not**:

- Prevent a fully privileged attacker from **deleting all data**.
- Prevent history rewriting if the attacker has both:
  - write access to the store **and**
  - the **HMAC secret key**.
- Replace SIEM/log pipelines. It complements them.

---

## 2. Versioning and Compatibility

This spec describes **PROVANCE v0.0.3**.

Key differences from **v0.0.2**:

- Introduction of a **monotonic `Sequence`** field.
- The signed content now includes `Sequence`.
- The **Single Writer** (background service) is the **only component allowed to seal entries** (assign `Sequence`, `PreviousHash`, `CurrentHash`).

> **Breaking change:**  
> Ledgers created with v0.0.2 are **not compatible** with v0.0.3.  
> v0.0.3 MUST use a **new ledger collection**, e.g. `ledger_entries_v1`.

---

## 3. Data Model

### 3.1 `LedgerEntry`

A `LedgerEntry` represents one event in the tamper-evident ledger.

Fields (C# model):

```csharp
public sealed class LedgerEntry
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    public string EventType { get; set; } = default!;
    public AuditedPayload? Payload { get; set; }

    public string PreviousHash { get; set; } = default!;
    public string CurrentHash { get; set; } = default!;
}
```

Constraints:

- `Id`: unique identifier (GUID).
- `Sequence`:
  - strictly monotonic, `>= 1`
  - assigned **only** by the Single Writer.
- `Timestamp`:
  - UTC (`DateTimeOffset.UtcNow`) at sealing time.
- `EventType`:
  - non-empty string describing the event category.
- `PreviousHash`:
  - for the first entry: the configured `GenesisHash`.
  - otherwise: `CurrentHash` of the previous entry in the chain.
- `CurrentHash`:
  - HMAC-SHA256 over a canonical JSON representation (see §4).
- `Payload`:
  - optional structured object (e.g. `AuditedPayload`).

### 3.2 `AuditedPayload` (example)

The protocol does not mandate a specific payload schema.  
The default `.NET` implementation uses:

```csharp
public sealed class AuditedPayload
{
    public string? ActorId { get; set; }
    public string? Description { get; set; }
    public string? CorrelationId { get; set; }
    public string? AdditionalData { get; set; }
}
```

Any serializable object can be used as the `payload` field in the signed content.

---

## 4. Canonical Serialization & Hashing

### 4.1 Overview

Each `LedgerEntry` is signed using **HMAC-SHA256**:

```csharp
CurrentHash = HMAC_SHA256(
    key = SecretKey,
    data = CanonicalJson(entry)
);
```

Where:

- `SecretKey` is a symmetric secret configured in `ProvanceOptions.SecretKey`.
- `CanonicalJson(entry)` is a deterministic JSON string built as described below.

Hashing must **exclude** `CurrentHash` itself.

### 4.2 Canonical JSON structure

For hashing, a temporary anonymous object is built with the following fields and **exact property names/order**:

```jsonc
{
  "sequence":   long,                 // entry.Sequence
  "id":         "guid-string",        // entry.Id
  "timestamp":  "ISO-8601",           // entry.Timestamp (UTC)
  "previousHash":"hex-string|null",   // lowercased
  "eventType":  "string",
  "payload":    { ... } | null
}
```

In C# (simplified):

```csharp
var entryToHash = new
{
    sequence     = entry.Sequence,
    id           = entry.Id,
    timestamp    = entry.Timestamp,
    previousHash = entry.PreviousHash?.ToLowerInvariant(),
    eventType    = entry.EventType,
    payload      = entry.Payload
};
```

Serialization uses `System.Text.Json` with options:

- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `WriteIndented = false`
- Encoder = `UnsafeRelaxedJsonEscaping`
- Default `DateTimeOffset` formatting (round-trip, ISO 8601 with offset, e.g. `2025-11-27T19:28:40.1234567+00:00`)

The resulting UTF-8 JSON string is the **canonical** message to be signed.

### 4.3 HMAC-SHA256 computation

Pseudocode:

```text
function CalculateHash(entry, secretKey):
    assert entry != null
    assert secretKey is not null/empty

    json = CanonicalJson(entry) // as in 4.2

    keyBytes  = UTF8(secretKey)
    dataBytes = UTF8(json)

    hashBytes = HMAC-SHA256(keyBytes, dataBytes)

    return LOWER_HEX(hashBytes) // 64 hex chars
```

This value is stored in `entry.CurrentHash`.

### 4.4 Genesis hash

`GenesisHash` is a **64-character hex string** configured at deployment:

- stored in `ProvanceOptions.GenesisHash`
- used as the **`PreviousHash` of the first entry** in the chain
- also used as the starting expected hash during verification (see §7)

Validation:

- must be 64 hex chars (`[0-9a-fA-F]{64}`)

---

## 5. Single Writer Model (v0.0.3)

### 5.1 Motivation

Under concurrent producers, having multiple writers update the chain directly can create:

- race conditions on the chain head
- inconsistent `PreviousHash`
- forks

v0.0.3 introduces a **Single Writer** background service (`LedgerWriterService`):

- producers enqueue **intents** (`LedgerTransactionContext`)
- the writer:
  - acquires an exclusive lease
  - sequences and seals all intents
  - persists them to the store
  - acknowledges back to the callers

### 5.2 Components

- **`ILedgerService` (producer)**  
  API-facing service; creates transaction contexts and enqueues them.

- **`IEntryQueue` (in-memory channel)**  
  Bounded channel with backpressure semantics.

- **`LedgerWriterService` (Single Writer)**  
  `BackgroundService` that:
  - acquires a distributed lease via `ILedgerStore.AcquireOrRenewLeaseAsync`
  - initializes local chain head (`PreviousHash`, `Sequence`)
  - drains the queue, seals entries, persists, acknowledges.

- **`ILedgerStore`**  
  Storage abstraction (MongoDB implementation in `Provance.Storage.MongoDB`).

### 5.3 Producer logic (`LedgerService.AddEntryAsync`)

High level flow:

1. Validate `eventType` and `payload`.
2. Create a `LedgerTransactionContext`:
   - `EventType`
   - `Payload`
   - `AckSource` (`TaskCompletionSource<LedgerEntry>`)
3. `EnqueueAsync(context, cancellationToken)`
   - may wait if queue is full (backpressure)
4. Wait for `context.AckSource.Task`:
   - completes when the writer has **sealed and persisted** the entry.
5. Return the sealed `LedgerEntry` to the caller.

> In v0.0.3, `AddEntryAsync` acknowledges **only after persistence** in the ledger store.

### 5.4 Writer loop (`LedgerWriterService`)

At startup:

1. Acquire a 30s lease (`LOCK_RESOURCE = "ledger_writer_lock_v1"`) using `ILedgerStore.AcquireOrRenewLeaseAsync`.
2. If lease cannot be acquired: **fail fast** (crash).
3. Start a heartbeat task to renew the lease periodically.
4. Read last entry:
   - `localPreviousHash = lastEntry?.CurrentHash ?? GenesisHash`
   - `localSequence = lastEntry?.Sequence ?? 0`

For each queued context:

1. Compute `nextSequence = localSequence + 1`.
2. Build `LedgerEntry`:

   ```csharp
   var entry = new LedgerEntry
   {
       Id          = Guid.NewGuid(),
       Sequence    = nextSequence,
       Timestamp   = DateTimeOffset.UtcNow,
       EventType   = context.EventType,
       Payload     = context.Payload,
       PreviousHash= localPreviousHash,
       CurrentHash = null
   };
   ```

3. Compute `CurrentHash = CalculateHash(entry, SecretKey)`.
4. Persist using `ILedgerStore.WriteEntryAsync` with retry (Polly exponential backoff).
5. If persistence succeeds:
   - `localPreviousHash = entry.CurrentHash`
   - `localSequence = nextSequence`
   - complete `context.AckSource.SetResult(entry)`
6. On failure after retries:
   - log error
   - `context.AckSource.TrySetException(ex)`
   - (service may then crash depending on the exception path)

On shutdown:

- stop queue writer (`CompleteWriter()`)
- cancel heartbeat
- exit the loop gracefully when the cancellation token is triggered.

---

## 6. Storage Contract (`ILedgerStore`)

### 6.1 Required operations

The store must implement:

```csharp
Task WriteEntryAsync(LedgerEntry entry, CancellationToken ct = default);
Task<LedgerEntry?> GetLastEntryAsync(CancellationToken ct = default);
Task<LedgerEntry?> GetEntryByIdAsync(Guid id, CancellationToken ct = default);
Task<IEnumerable<LedgerEntry>> GetAllEntriesAsync(CancellationToken ct = default);
Task<bool> AcquireOrRenewLeaseAsync(string resourceName, string workerId, TimeSpan duration, CancellationToken ct = default);
```

Semantics:

- `WriteEntryAsync`:
  - must persist all fields atomically.
- `GetLastEntryAsync`:
  - must return the **current chain head** (max `Sequence`, then `Id`).
- `GetAllEntriesAsync`:
  - must return **all entries** sorted by:
    - `Sequence` ascending
    - then `Id` ascending
- `GetEntryByIdAsync`:
  - standard lookup by `Id`.
- `AcquireOrRenewLeaseAsync`:
  - must implement a **best-effort exclusive lease**:
    - same `resourceName` cannot be held concurrently by two different `workerId` while not expired.
    - must allow renewing if the lease is held by the same `workerId`.
    - must allow taking over an **expired** lease.

### 6.2 MongoDB implementation (v0.0.3)

`Provance.Storage.MongoDB.MongoLedgerStore`:

- **Collection for entries**: configurable name e.g. `ledger_entries_v1`
- **Collection for locks**: fixed name `provance_locks`

Indexes created on startup:

- `headIndex`:

  ```text
  { Sequence: -1, Id: -1 }
  ```

- `idIndex`:

  ```text
  { Id: 1 }
  ```

- `sequenceUniqueIndex` (unique):

  ```text
  { Sequence: 1 } with { unique: true }
  ```

These enforce:

- fast head lookup
- lookup by `Id`
- uniqueness of `Sequence` (deterministic ordering guarantee)

> Because v0.0.3 requires a **new collection** (e.g. `ledger_entries_v1`), the unique `Sequence` index is safe and expected to succeed.

The lock/lease logic uses a document in `provance_locks`:

```jsonc
{
  "_id": "ledger_writer_lock_v1",
  "holder": "worker-id",
  "expiresAt": "ISO-8601 string",
  "lastHeartbeat": "ISO-8601 string"
}
```

- Renew/Acquire is implemented as:
  - update if `expiresAt < now` OR `holder == workerId`
  - otherwise try to `insert` a new document
  - duplicate-key errors indicate that another worker holds the lease.

---

## 7. Verification Algorithm

`ILedgerService.VerifyChainIntegrityAsync` performs global verification.

Input:

- `SecretKey`
- `GenesisHash`
- all entries from store (`GetAllEntriesAsync`), already sorted by `Sequence` then `Id`.

Algorithm:

1. Load all entries into `List<LedgerEntry>` `entries`.
2. If `entries.Count == 0`:
   - return `(true, "Ledger is empty, integrity assumed (Genesis Hash is the current reference).")`
3. Initialize:

   ```csharp
   string expectedPreviousHash = GenesisHash;
   ```

4. For each `entry` in `entries` (in order):

   - **Chain continuity check**:

     ```csharp
     if (entry.PreviousHash != expectedPreviousHash)
         return (false, $"Chain link broken at entry ID {entry.Id}. Expected PreviousHash: {expectedPreviousHash}, but found: {entry.PreviousHash}.");
     ```

   - **Data integrity check**:

     ```csharp
     var calculatedHash = HashUtility.CalculateHash(entry, SecretKey);
     if (calculatedHash != entry.CurrentHash)
         return (false, $"Data tampering detected in entry ID {entry.Id}. Calculated hash: {calculatedHash} does not match stored hash: {entry.CurrentHash}.");
     ```

   - Update:

     ```csharp
     expectedPreviousHash = calculatedHash;
     ```

5. If all entries succeed:

   - return `(true, "Chain integrity successfully verified. All entries are valid and authentically signed.")`.

The implementation may periodically check the `CancellationToken` for long chains.

---

## 8. Configuration

### 8.1 Protocol options (`ProvanceOptions`)

Example `appsettings.json` section:

```json
{
  "ProvanceProtocol": {
    "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
    "SecretKey": "CHANGE_ME_IN_PROD"
  }
}
```

Constraints:

- `GenesisHash`:
  - required, 64 hex chars
- `SecretKey`:
  - required, non-empty string
  - should be random and stored securely (KMS, vault, etc.)

### 8.2 MongoDB configuration

```json
{
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries_v1"
  }
}
```

> For v0.0.3+, use a **new collection** name (e.g. `ledger_entries_v1`) instead of reusing a v0.0.2 ledger.

---

## 9. Failure Modes & Limitations (v0.0.3)

### 9.1 Durability

v0.0.3:

- acknowledges API calls **after persistence** in the ledger store.
- does **not yet** implement a **durable outbox** / WAL:
  - if the application crashes **before** the writer consumes an enqueued intent, that intent is lost.
  - this is acceptable for v0.0.3 (pre-release) but will be addressed in v0.0.4+.

### 9.2 Single Writer & lease

- Only one instance must hold the `ledger_writer_lock_v1` lease at a time.
- If the lock is lost (e.g. Mongo unavailable, network issues):
  - `LedgerWriterService` treats this as a **fatal error** and should crash (or be restarted by the orchestrator).
- Multi-instance deployments must ensure:
  - only **one active writer** at a time per ledger.

### 9.3 Attack model limitations

The protocol detects:

- tampering with existing entries.
- reordering that breaks `PreviousHash` continuity.

It does **not** protect against:

- deletion of the entire ledger (and lock collection).
- attacker with full DB access **and** `SecretKey` who recomputes hashes consistently.

Mitigations are out-of-scope for this version:

- off-site backups, WORM storage, external anchoring, strict key management.

---

## 10. Roadmap Notes (towards v1.0.0)

Planned for future versions (not yet implemented in v0.0.3):

- **Durable outbox** + replay on startup.
- Configurable acknowledgement levels:
  - “enqueued only”
  - “durable outbox”
  - “fully stored in ledger”
- Canonical serialization test vectors + cross-language reproducibility (Java/Rust).
- Multi-instance distributed writers with stronger guarantees.
- Optional anchoring to immutable / external systems.

---

_This document describes the **.NET reference implementation** at version **0.0.3**.  
Other languages (Java, Rust, …) must conform to the hashing and sequencing rules defined in §3–§4 to be considered protocol-compatible._
