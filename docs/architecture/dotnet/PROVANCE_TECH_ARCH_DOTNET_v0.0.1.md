# 🔱 PROVANCE Technical Architecture (C# .NET 8 Implementation)

**Version:** 0.0.1
**Based on:** PROVANCE Protocol Specification (V0.0.1)

---

## 1. The Producer/Consumer Pattern (Zero-Blocking)

The PROVANCE protocol is implemented using an asynchronous Producer/Consumer model, primarily utilizing `System.Threading.Channels`. This design is critical to ensuring the **Zero-Blocking Principle** by decoupling the fast-paced web API thread from the slower, permanent database write operation.



### 1.1. The Producer Chain (Web API Thread)

This sequence runs synchronously within the application request pipeline.

| Component | Role | Description |
| :--- | :--- | :--- |
| **`ProvanceLoggerMiddleware`** | **Event Creation** | Intercepts the HTTP request, formats event data into an `AuditedPayload`, and passes it to the Sealer. |
| **`ILedgerService` (Sealer)** | **Cryptographic Sealing** | Fetches the `PreviousHash` from the store, calculates the final `CurrentHash` for the new `LedgerEntry`, and immediately pushes the sealed entry into the queue. |
| **Key Operation:** The `SealEntryAsync` call completes immediately, releasing the HTTP request thread before any database I/O takes place. |

### 1.2. The Buffer: `IEntryQueue`

The queue manages the flow of data between the Producer and the Consumer.

| Feature | Implementation | Rationale |
| :--- | :--- | :--- |
| **Core Technology** | `System.Threading.Channels` (specifically, a Bounded Channel). | Provides high-performance, thread-safe buffering optimized for asynchronous I/O. |
| **Backpressure** | `BoundedChannelFullMode.Wait` is set on a capacity of `100,000` entries. | If the queue is full (e.g., if the database is down), the `EnqueueEntryAsync` call will **block the producer**. This is a safety measure to prevent the web server's memory from being exhausted. |

### 1.3. The Consumer: `LedgerWriterService`

This runs as a long-running background service, completely independent of the web request lifetime.

| Component | Role | Description |
| :--- | :--- | :--- |
| **`LedgerWriterService`** | **Persistence Daemon** | An `IHostedService` that uses the `IEntryQueue.Reader` and `WaitToReadAsync()` in a perpetual loop to retrieve sealed entries. It also overrides `StopAsync()` to call `IEntryQueue.CompleteWriter()` for graceful shutdown.|
| **`ILedgerStore`** | **Data Persistence Layer** | This interface handles the permanent write operation (`WriteEntryAsync`). The implementation (e.g., MongoDB, PostgreSQL) is responsible for ensuring data durability and atomicity. |

---

## 2. Integrity and Verification Implementation

### 2.1. Hash Calculation

The `HashUtility.CalculateHash` method must follow the specification:

$$\text{CurrentHash} = \text{SHA256}(\text{Id} + \text{Timestamp} + \text{EventType} + \text{Payload} + \text{PreviousHash})$$

It serializes the entire `LedgerEntry` object (excluding its own `CurrentHash`) into a canonical, ordered JSON string before applying SHA-256 to ensure a consistent output across environments.

### 2.2. Verification Logic

The `LedgerService.VerifyChainIntegrityAsync` method performs two core checks in a single pass over the ledger:

1.  **Chain Link Check:** Compares the stored `E_i.PreviousHash` with the `CurrentHash` of the previously verified entry (`E_{i-1}.CurrentHash`).
2.  **Data Tamper Check:** Recalculates the hash of $E_i$'s payload and metadata, and compares it against the stored $E_i.\text{CurrentHash}$.

If either check fails, the method returns `(false, reason)` immediately, fulfilling the **Tamper Evidence** principle.