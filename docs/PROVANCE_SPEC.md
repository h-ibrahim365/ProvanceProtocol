# 🔱 PROVANCE Protocol Specification (V0.0.1)

This document defines the core architecture, data structures, and cryptographic rules of the PROVANCE Protocol. It serves as the single source of truth for implementing the ledger mechanism in any language or environment.

**Version:** 0.0.1 (Initial Core Release)
**Date:** 2025-11-23

---

## 1. Core Principles

The Provance Protocol is built on two unassailable principles:

1.  **Tamper Evidence:** Any modification to a past ledger entry must instantly and irreversibly invalidate the entire chain, making the tampering immediately verifiable.
2.  **Zero-Blocking:** The process of logging an event must not block the main application thread (e.g., the API request thread). Hashing and persistence must be delegated to background services.

---

## 2. Data Structure: LedgerEntry

The fundamental unit of the ledger is the `LedgerEntry`. Its structure is immutable once sealed.

| Field | Data Type | Description | Role in Hashing |
| :--- | :--- | :--- | :--- |
| **Id** | `Guid` | Unique identifier for the entry. | Included |
| **Timestamp** | `DateTimeOffset` | Precise time of event creation. | Included |
| **EventType** | `string` | Classification of the event (e.g., `USER_LOGIN`, `HTTP_REQUEST_SUCCESS`). | Included |
| **Payload** | `object (JSON/Text)` | The structured data associated with the event (e.g., request details, user ID, business data). | Included |
| **PreviousHash** | `string (SHA-256)` | Hash of the **previous** entry in the chain. | Included |
| **CurrentHash** | `string (SHA-256)` | The cryptographic seal of **this** entry. | Excluded (Calculated) |

### Hashing Formula

The `CurrentHash` is calculated using the following concatenated data elements, serialized to a consistent string representation (e.g., JSON or ordered string) before applying the SHA-256 algorithm:

$$\text{CurrentHash} = \text{SHA256}(\text{Id} + \text{Timestamp} + \text{EventType} + \text{Payload} + \text{PreviousHash})$$

---

## 3. Cryptographic Chain

### 3.1. Genesis Entry

The first entry in any PROVANCE ledger is the **Genesis Entry**.

* Its `PreviousHash` MUST be a hardcoded, publicly known, and non-repeating constant (e.g., a string of zeros or a unique initial hash set during deployment).
* This hash is defined in the application configuration (`ProvanceOptions.GenesisHash`). **It must never change after initial deployment.**

### 3.2. Chain Linkage

Every subsequent entry ($E_n$) validates the link to the preceding entry ($E_{n-1}$):

$$\text{E}_n.\text{PreviousHash} = \text{E}_{n-1}.\text{CurrentHash}$$

This creates the unbreakable, tamper-evident cryptographic chain.

---

## 4. Architectural Overview (Zero-Blocking Pattern)

The protocol relies on a Producer/Consumer pattern to isolate the synchronous application flow (e.g., ASP.NET Core API) from the asynchronous, computationally heavy ledger writing process.

### Architecture Flow


[Image of a data flow diagram]


The flow is strictly non-blocking:

1.  **Producer (Middleware/API)**: An event occurs (e.g., HTTP request). The `ProvanceLoggerMiddleware` formats the data and calls `ILedgerService.SealEntryAsync`.
2.  **Sealer (`ILedgerService`)**: Fetches the last hash, calculates the new hash, and places the sealed entry into the `IEntryQueue`. **This is the critical non-blocking operation.**
3.  **Buffer (`IEntryQueue`)**: A high-capacity, thread-safe queue buffering data between the web API (fast) and the database (slow).
4.  **Consumer (`LedgerWriterService`)**: A dedicated background service (`IHostedService`) that reads sealed entries from the queue and handles the persistent write operation to the `ILedgerStore`.

### Component Roles

1.  **`ProvanceLoggerMiddleware` (Producer):** Intercepts requests, formats the event data, and calls `ILedgerService.SealEntryAsync`.
2.  **`ILedgerService` (Sealer):** Manages cryptographic chaining (fetching `PreviousHash`, calculating `CurrentHash`) and enqueuing the final entry.
3.  **`IEntryQueue` (Buffer):** Ensures the API is never blocked by database latency.
4.  **`LedgerWriterService` (Consumer):** Performs the persistent write operation to the `ILedgerStore`.

---

## 5. Integrity Verification

The `ILedgerService.VerifyChainIntegrityAsync` method provides a comprehensive check of the ledger's integrity.

### Verification Steps

1.  Retrieve all ledger entries *in chronological and chained order* from the `ILedgerStore`.
2.  Initialize the expected hash with the **Genesis Hash**.
3.  Iterate through every entry ($E_i$):
    a. **Previous Hash Check (Chain Linkage):** Verify that $E_i.\text{PreviousHash}$ matches the `CurrentHash` of the previously validated entry ($E_{i-1}$) or the Genesis Hash.
    b. **Data Integrity Check (Tamper Evidence):** Recalculate the hash of the entry's original data and compare it against the stored $E_i.\text{CurrentHash}$.
    c. **Failure:** If either check fails, the verification stops immediately, an anomaly alert is raised (e.g., `LedgerTamperedException`), and the reason (broken link or data corruption) is reported.
4.  **Success:** If the loop completes without failure, the ledger is cryptographically sound.

## 6. Versioning & Roadmap

This specification targets the V0.0.1 feature set. All future updates to the protocol (e.g., Merkle Tree Archival, External Anchoring) will be documented in subsequent versions of this file (V0.0.2, V1.0.0, etc.).

**(Reference the core project's `README.md` for the current roadmap and future features.)**