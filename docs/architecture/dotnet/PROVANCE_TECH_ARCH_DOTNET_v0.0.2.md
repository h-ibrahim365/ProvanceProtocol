# 🔱 PROVANCE Protocol Specification (V0.0.2)

This document defines the core architecture, data structures, and cryptographic rules of the PROVANCE Protocol.
It serves as the single source of truth for implementing the ledger mechanism in any language or environment.

**Version:** 0.0.2 (Production Readiness — Data Layer + Resilience)  
**Date:** 2025-11-26

---

## 1. Core Principles

The PROVANCE Protocol is built on two unassailable principles:

1. **Tamper Evidence**  
   Any modification to a past ledger entry must instantly invalidate verification of the chain.

2. **Decoupled Logging (Async Producer/Consumer)**  
   Logging must be isolated from the persistent write path: the request thread should not wait on database I/O.
   Under sustained overload, the system may apply **backpressure** to protect memory (bounded queues).

> **Terminology note:** “Zero-Blocking” means *no database I/O on the request thread*.  
> With bounded buffering, enqueue can still await when saturated (intentional backpressure).

---

## 2. Data Structure: `LedgerEntry`

The fundamental unit of the ledger is the `LedgerEntry`. Its structure is immutable once sealed.

| Field | Data Type | Description | Role in Hashing |
| :--- | :--- | :--- | :--- |
| **Id** | `Guid` | Unique identifier for the entry. | Included |
| **Timestamp** | `DateTimeOffset` | Precise time of event creation (UTC recommended). | Included |
| **EventType** | `string` | Classification of the event (e.g., `USER_LOGIN`, `HTTP_REQUEST_SUCCESS`). | Included |
| **Payload** | `object (JSON/Text)` | Structured data associated with the event. | Included |
| **PreviousHash** | `string (hex, 256-bit)` | Hash of the **previous** entry in the chain. | Included |
| **CurrentHash** | `string (hex, 256-bit)` | Cryptographic seal of **this** entry. | Excluded (Calculated) |

---

## 3. Cryptography

### 3.1. Genesis Hash (Chain Root)

The first logical link in any PROVANCE ledger is the **Genesis Hash**:

- `GenesisHash` MUST be a hardcoded, non-repeating constant (commonly 64 hex chars of `0`).
- It is configured in `ProvanceOptions.GenesisHash`.
- **It must never change after initial deployment**, otherwise historical verification becomes impossible.

### 3.2. Hash Algorithm (V0.0.2)

**V0.0.2 uses HMAC-SHA256**, not plain SHA-256.

Why:
- plain SHA-256 proves *integrity* (tampering changes the hash),
- HMAC-SHA256 adds *authenticity* (entries cannot be forged without the secret key).

`SecretKey` is configured in `ProvanceOptions.SecretKey` and MUST NOT be empty in production.

### 3.3. Hashing Formula (HMAC-SHA256)

The `CurrentHash` is computed over a canonical representation of the entry **excluding** its own `CurrentHash`:

```
CurrentHash = HMAC_SHA256( CanonicalSerialize(Id, Timestamp, PreviousHash, EventType, Payload), SecretKey )
```

- Output format: **lowercase hex** (64 characters).
- The `CurrentHash` field is excluded from the signed payload to avoid recursion.

### 3.4. Canonical Serialization (V0.0.2 Rules)

To keep hashes stable within an implementation and prepare cross-language support, the entry is serialized as JSON with these rules:

- Encoding: UTF-8
- JSON: no indentation / no comments
- Property naming: `camelCase`
- **Field order for the signed object MUST be:**
  1. `id`
  2. `timestamp`
  3. `previousHash`
  4. `eventType`
  5. `payload`

> Cross-language byte-for-byte canonicalization is tricky for arbitrary payload objects.
> Official “test vectors” and stricter canonicalization rules are planned for later versions (see roadmap).

---

## 4. Cryptographic Chain

### 4.1. Chain Linkage

Every entry `E_n` points to the hash of the entry before it:

```
E_n.PreviousHash = E_(n-1).CurrentHash
```

If any entry is modified, recomputation breaks either:
- the link (`PreviousHash` mismatch), or
- the signature (`CurrentHash` mismatch).

### 4.2. First Entry Rule

If the ledger is empty:
- `PreviousHash` MUST be set to `GenesisHash`.

---

## 5. Architectural Overview (Async Producer/Consumer Pattern)

The protocol uses a Producer/Consumer architecture to isolate the request path from persistent I/O.

### Flow

1. **Producer (Middleware/API)**  
   An event occurs (e.g., HTTP request). The producer formats the event data and calls the sealing API.

2. **Sealer (`ILedgerService`)**  
   Retrieves the last entry hash (or GenesisHash), calculates `CurrentHash` (HMAC), and enqueues the sealed entry into `IEntryQueue`.

3. **Buffer (`IEntryQueue`)**  
   A high-capacity, thread-safe queue (typically `System.Threading.Channels`) buffering the fast producer and slower persistence.  
   - Bounded queues may apply **backpressure** when full.

4. **Consumer (`LedgerWriterService`)**  
   A dedicated background service (`IHostedService`) that:
   - drains the queue,
   - writes entries to the configured `ILedgerStore`,
   - retries transient failures using a retry policy (e.g., exponential backoff).

### Component Roles

- `ProvanceLoggerMiddleware` (Producer): intercepts requests and emits audit events.
- `ILedgerService` (Sealer): manages chaining + cryptographic sealing (HMAC) and enqueuing.
- `IEntryQueue` (Buffer): decouples request speed from store latency.
- `LedgerWriterService` (Consumer): persists entries; includes resiliency (retry).

---

## 6. Integrity Verification

`ILedgerService.VerifyChainIntegrityAsync` provides a comprehensive integrity check.

### Verification Steps

1. Retrieve all ledger entries from `ILedgerStore`.
2. Sort entries into chronological order (protocol requires chain verification from oldest to newest).
3. Initialize `expectedPreviousHash = GenesisHash`.
4. For each entry `E_i`:
   - **Chain Link Check:** `E_i.PreviousHash == expectedPreviousHash`
   - **Signature Check:**  
     Recompute `calculated = HMAC_SHA256(CanonicalSerialize(E_i minus CurrentHash), SecretKey)`  
     Verify `calculated == E_i.CurrentHash`
   - Update: `expectedPreviousHash = E_i.CurrentHash`
5. If all checks pass, the ledger is valid.

---

## 7. Data Layer (Production Readiness)

V0.0.2 introduces the expectation of a production-grade persistence layer.

### `ILedgerStore` Requirements

An `ILedgerStore` implementation SHOULD:
- persist entries in append-only fashion (logical append)
- support retrieving the last entry efficiently
- support retrieving all entries for verification

A reference implementation is provided for MongoDB (`Provance.Storage.MongoDB`).

---

## 8. Versioning & Roadmap (Spec)

This specification targets the V0.0.2 feature set.

Upcoming protocol evolutions:
- **V0.0.3:** concurrency correctness (avoid forks) & strict shutdown behavior
- **V0.0.6:** canonicalization hardening + official test vectors (polyglot ready)
- **V0.0.8:** Merkle batching / checkpoints for pruning foundations
- **V1.0.0:** stability guarantees, hardened threat model, and external trust mechanisms

(Reference the core project’s `README.md` for the current roadmap and milestones.)
