<p align="center">
  <img src="./icons/provanceProtocol.png" alt="PROVANCE Protocol logo" width="160" />
</p>

<h1 align="center">🔱 PROVANCE Protocol</h1>

<p align="center">
  <b>Tamper-evident audit trails for .NET — protocol-first, verifiable, and production-minded.</b><br/>
  Cryptographically detect log tampering using hash-chaining (HMAC-SHA256) and deterministic ordering.
</p>

<p align="center">
  <a href="https://github.com/sponsors/h-ibrahim365">
    <img src="https://img.shields.io/badge/sponsor-30363D?style=for-the-badge&logo=GitHub-Sponsors&logoColor=white" alt="Sponsor" />
  </a>
  <a href="https://github.com/h-ibrahim365/ProvanceProtocol/actions">
    <img src="https://img.shields.io/badge/Build-Passing-brightgreen" alt="Build Status" />
  </a>
  <a href="LICENSE.md">
    <img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" />
  </a>
</p>

---

## Packages

| Package | Status | Version |
| :--- | :--- | :--- |
| **Provance.Core** (NuGet) | Active | [![NuGet](https://img.shields.io/nuget/v/Provance.Core.svg?style=flat)](https://www.nuget.org/packages/Provance.Core) |
| **Provance.AspNetCore.Middleware** (NuGet) | Active | [![NuGet](https://img.shields.io/nuget/v/Provance.AspNetCore.Middleware.svg?style=flat)](https://www.nuget.org/packages/Provance.AspNetCore.Middleware) |
| **Provance.Storage.MongoDB** (NuGet) | Active | [![NuGet](https://img.shields.io/nuget/v/Provance.Storage.MongoDB.svg?style=flat)](https://www.nuget.org/packages/Provance.Storage.MongoDB) |
| **Provance (Java)** (Maven) | Planned | After **v1.0.0** |
| **Provance (Rust)** (Cargo) | Planned | After **v1.0.0** |

---

## Table of contents

- [The Problem](#the-problem)
- [What PROVANCE is (and isn’t)](#what-provance-is-and-isnt)
- [Core features (current)](#core-features-current)
- [v0.0.3 changes](#v003-changes)
- [Breaking change (v0.0.2 → v0.0.3)](#breaking-change-v002--v003)
- [Security model (threat model)](#security-model-threat-model)
- [Quick start (.NET)](#quick-start-net)
  - [Installation](#installation)
  - [Configuration (appsettings.json)](#configuration-appsettingsjson)
  - [Minimal API example](#minimal-api-example)
- [Deployment & guarantees](#deployment--guarantees)
- [Roadmap (Premium V1)](#roadmap-premium-v1)
- [After v1.0.0](#after-v100)
- [Java & Rust SDK plan](#java--rust-sdk-plan)
- [License](#license)

---

## The Problem

Standard audit logs are easy to **modify**, **backdate**, or **delete** — especially by privileged insiders or attackers who gained access to your database.

**PROVANCE** turns your application audit events into a **tamper-evident ledger**: each entry depends on the previous one via cryptographic chaining.
If someone changes an older record, verification fails.

---

## What PROVANCE is (and isn’t)

✅ PROVANCE is:
- A **protocol + SDKs** to create **verifiable, tamper-evident audit trails**
- A **library you can integrate quickly** (ASP.NET Core middleware + stores)

❌ PROVANCE is not:
- A “blockchain network”
- A full prevention system that stops deletion by a root/admin attacker
- A replacement for SIEM/log pipelines (it complements them)

---

## Core features (current)

- **Tamper-evident hash chaining (HMAC-SHA256)**
  Each entry has a `CurrentHash` computed from its signed content + `PreviousHash`, using **HMAC-SHA256** with a secret key.

- **Deterministic ordering (`Sequence`)**
  A monotonic `Sequence` is assigned by the Single Writer. This avoids timestamp collisions and makes verification deterministic.

- **Single Writer (anti-fork)**
  Concurrent producers are linearized through one background writer loop, preventing ledger forks under load.

- **Store abstraction (`ILedgerStore`)**
  MongoDB store is available today.

- **Integrity verification API**
  Verify chain integrity and pinpoint where it breaks.

---

## v0.0.3 changes

Implemented in **v0.0.3**:
- single-writer sequencing + deterministic ordering via `Sequence`
- hashing updated to include `Sequence` (ordering changes become tamper-evident)
- stores updated to use `Sequence` for ordering (`GetLastEntryAsync`, `GetAllEntriesAsync`)
- verification runs in `Sequence` order and checks:
  - chain continuity (`PreviousHash`)
  - cryptographic integrity (recomputed HMAC)

---

## Breaking change (v0.0.2 → v0.0.3)

v0.0.3 changes the protocol in a way that makes existing ledgers from v0.0.2 **incompatible**:

- `Sequence` is now part of the ordering model.
- The HMAC signed content now includes `Sequence`.

### Required migration (recommended and supported path)

**Start a new ledger collection.** Do not reuse a v0.0.2 collection.

1. Choose a new collection name (example: `ledger_entries_v1`)
2. Update your config (`MongoDb:CollectionName`)
3. Deploy / run the app

This keeps your v0.0.2 ledger as an archived historical ledger, and v0.0.3+ as the new deterministic ledger.

> v0.x is pre-release: breaking changes can happen between minor versions.

---

## Security model (threat model)

PROVANCE aims to **detect tampering**.

✅ Detects:
- modifications to stored entries (payload/type/timestamps/sequence/hashes)
- broken chain continuity (mismatched `PreviousHash` / `CurrentHash`)
- reordering that breaks continuity

⚠️ Does **not** prevent:
- full deletion of the database (or an outbox) by a fully privileged attacker
- rewriting history if an attacker has both **write access** and the **HMAC secret key**

Mitigations (ops guidance / roadmap):
- strict key management guidance (KMS, rotation, least privilege)
- optional external anchoring / immutable backends (after v1)

---

## Quick start (.NET)

### Installation

```bash
dotnet add package Provance.Core
dotnet add package Provance.AspNetCore.Middleware
# Optional (recommended for prod)
dotnet add package Provance.Storage.MongoDB
```

### Configuration (appsettings.json)

```json
{
  "ProvanceProtocol": {
    "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
    "SecretKey": "CHANGE_ME_IN_PROD"
  },
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries_v1"
  }
}
```

### Minimal API example

```csharp
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Core.Services.Interfaces;
using Provance.Storage.MongoDB.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Optional: MongoDB storage (recommended for production)
// 1) Install:
//    dotnet add package Provance.Storage.MongoDB
// 2) Uncomment the next line to enable MongoDB persistence:
// builder.Services.AddProvanceMongoStorage(builder.Configuration);

// Core services + options
builder.Services.AddProvanceLogging(options =>
{
    var cfg = builder.Configuration.GetSection("ProvanceProtocol");
    options.GenesisHash = cfg["GenesisHash"] ?? string.Empty;
    options.SecretKey = cfg["SecretKey"] ?? string.Empty;
});

var app = builder.Build();

app.UseProvanceLogger();

app.MapGet("/api/ledger/verify", async (ILedgerService ledgerService) =>
{
    var (isValid, reason) = await ledgerService.VerifyChainIntegrityAsync();
    return isValid
        ? Results.Ok(new { IsValid = true, Message = "Chain integrity verified." })
        : Results.Conflict(new { IsValid = false, Message = $"Integrity compromised: {reason}" });
});

app.Run();
```

---

## Deployment & guarantees

### Writer model (current)
**v0.0.3 is intentionally single-writer / single-instance oriented.**
You can still handle many concurrent HTTP requests; correctness is guaranteed by sequencing ledger appends through a single writer.

### Acknowledgement semantics (current)
In v0.0.3 the API call returns only after the Single Writer has **persisted** the entry.
This is the strongest correctness mode, but it increases end-to-end latency.

Roadmap:
- v0.0.4 introduces configurable acknowledgement modes and a durable outbox.

### Overload / saturation (roadmap)
Overload handling will be formalized in v0.0.4:
- **FailFast**: return 503/429 when you cannot guarantee durability (no silent loss)
- **Backpressure**: async waiting until there is capacity

---

## Roadmap (Premium V1)

This roadmap is designed so that **v1.0.0** means:
- predictable behavior under concurrency,
- clear guarantees,
- crash-safe durability (when enabled),
- stable protocol surface for future Java/Rust SDKs.

### ✅ v0.0.3 — Correctness under concurrency (anti-fork)
- Guarantee a **linear chain** under high concurrent producers (no forks)
- Single Writer sequencing + deterministic ordering (`Sequence`)
- Verification and store ordering updated

### 🛡️ v0.0.4 — Durable non-blocking (Outbox + replay)
- Introduce acknowledgement levels (e.g. persisted outbox vs stored in ledger)
- Durable outbox (WAL/spool) + replay on startup
- Overload policies: FailFast / Backpressure (no silent loss)
- Checkpointing + tail/partial-write recovery + fail-closed option

### 🔒 v0.0.5 — Canonical serialization + official test vectors
- Canonical rules (timestamps, UUIDs, field order, casing, encoding)
- Test vectors (input → canonical bytes → expected hash)
- Interop contract for Java/Rust starts here

### 📊 v0.0.6 — Observability & production readiness
- Health checks (writer, store, outbox)
- Metrics (outbox lag, queue depth, retry count, write latency)
- Benchmarks in CI (perf regression detection)

### 🚀 v1.0.0 — Premium stable release
Release criteria:
- Concurrency correctness proven by tests (no forks)
- Durable outbox mode with replay + checkpoints (when enabled)
- Canonical serialization + test vectors published
- Clear guarantees and limitations documented

---

## After v1.0.0

- Merkle batching / checkpoints / proofs (performance + partial verification proofs)
- Distributed writers / multi-instance correctness
- Anchoring providers (optional external anchoring / immutable stores)
- Additional stores (PostgreSQL, etc.)

---

## Java & Rust SDK plan

Java and Rust SDKs will be developed **after v1.0.0** once the protocol is stable.

Why after v1.0.0?
- canonical serialization must be finalized
- test vectors must exist
- CI must run conformance tests across languages

First deliverable (portable core):
- `seal(draft, key) -> sealedEntry`
- `verify(chain, key) -> ok / failure(index, reason)`
- same canonicalization + same test vectors as .NET

---

## License

MIT — see [LICENSE.md](LICENSE.md).
