# 🔱 PROVANCE Protocol — The Bulletproof Data Ledger

**Cryptographically guarantee the integrity of your application’s audit logs (tamper-evident).**

[![Sponsor](https://img.shields.io/badge/sponsor-30363D?style=for-the-badge&logo=GitHub-Sponsors&logoColor=#white)](https://github.com/sponsors/h-ibrahim365)
[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen)](https://github.com/h-ibrahim365/ProvanceProtocol/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.md)

| Package | Status | Version |
| :--- | :--- | :--- |
| **C# (NuGet)** | Active | [![NuGet](https://img.shields.io/nuget/v/Provance.Core.svg?style=flat)](https://www.nuget.org/packages/Provance.Core) |
| **Java (Maven)** | Planning | Soon |
| **Rust (Cargo)** | Planning | Soon |

---

## Table of Contents

- [The Problem](#the-problem)
- [Core Features (V0.0.2)](#core-features)
- [Security Model (Threat Model)](#security-model)
- [Quick Start (.NET)](#quick-start)
  - [Installation](#installation)
  - [Configuration (appsettings.json)](#configuration)
  - [Program.cs (Minimal API example)](#programcs-minimal-api-example)
- [Roadmap (V0.0.3 → V1.0.0)](#roadmap)
- [After V1.0.0 (V2.0.0+)](#after-v1)
- [When should you publish V1.0.0?](#when-v1)
- [Contribution and Development](#contributing)
- [Sponsorship](#sponsorship)
- [License](#license)

---

<a id="the-problem"></a>
## ⚡️ The Problem: Logs Lie. The Solution: Cryptography.

In enterprise and compliance-driven applications, standard audit logs are **mutable** and can be **deleted or modified** by an attacker. This compromises forensic trust and compliance efforts (GDPR, ISO 27001).

**PROVANCE** transforms audit logs into a **tamper-evident ledger** by applying **cryptographic hash-chaining** at the application layer. If a past record is modified, the chain breaks and verification fails.

---

<a id="core-features"></a>
## ✅ Core Features — V0.0.2 (Production Readiness)

- **🛡️ Tamper-Evident Hash-Chaining (HMAC-SHA256)**  
  Each entry is sealed with a `CurrentHash` derived from its content + `PreviousHash`, signed with **HMAC-SHA256** using a secret key.

- **⚡️ Async Producer/Consumer Pipeline**  
  Audit events are buffered via `System.Threading.Channels` and persisted by a dedicated background consumer (`LedgerWriterService`).

- **🔁 Resilient Persistence**  
  Automatic retry with exponential backoff in `LedgerWriterService` for transient store failures.

- **🗄️ MongoDB Store (ILedgerStore)**  
  A production-ready `ILedgerStore` implementation for MongoDB (persistence + scalability).

### A note about “Zero-Blocking”
PROVANCE decouples your request pipeline from **database writes** by using a background consumer.  
If you use a **bounded** channel with backpressure, the system may intentionally slow producers under sustained overload to protect memory.  
A “strict non-blocking” mode (always returns immediately) requires a **durable fallback** (outbox/spool) — planned toward the beta line.

---

<a id="security-model"></a>
## 🔍 Security Model (Threat Model)

✅ Detects:
- modification of past entries (payload, type, timestamps, hashes)
- chain breaks / partial tampering

⚠️ Does **NOT** prevent:
- total deletion of the underlying database by a fully privileged attacker

Planned mitigations:
- external anchoring / immutable storage (WORM) / periodic checkpoints

---

<a id="quick-start"></a>
## 🛠️ Quick Start (C# / .NET)

<a id="installation"></a>
### 1) Installation

```bash
dotnet add package Provance.Core
dotnet add package Provance.AspNetCore.Middleware
# Optional (recommended for prod)
dotnet add package Provance.Storage.MongoDB
```

<a id="configuration"></a>
### 2) Configuration (appsettings.json)

```json
{
  "ProvanceProtocol": {
    "GenesisHash": "0000000000000000000000000000000000000000000000000000000000000000",
    "SecretKey": "CHANGE_ME_IN_PROD"
  },
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "provance",
    "CollectionName": "ledger_entries"
  }
}
```

<a id="programcs-minimal-api-example"></a>
### 3) Program.cs (Minimal API example)

```csharp
using Microsoft.AspNetCore.Mvc;
using Provance.AspNetCore.Middleware.Extensions;
using Provance.Core.Services.Interfaces;
using Provance.Storage.MongoDB.Extensions;

var builder = WebApplication.CreateBuilder(args);

// OPTIONAL: enable MongoDB storage (otherwise fallback In-Memory store is used)
// builder.Services.AddProvanceMongoStorage(builder.Configuration);

// Register Provance services (queue + ledger service + background writer + fallback store if needed)
builder.Services.AddProvanceLogging(options =>
{
    var provanceConfig = builder.Configuration.GetSection("ProvanceProtocol");

    options.GenesisHash = provanceConfig["GenesisHash"] ?? string.Empty;
    options.SecretKey = provanceConfig["SecretKey"] ?? string.Empty;
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

<a id="roadmap"></a>
## 📈 Roadmap (V0.0.3 → V1.0.0)

### 🛣️ V0.0.3 — Correctness under Concurrency
- Guarantee a **linear chain** under high concurrent producers (avoid “forks”).
- Strict cancellation + shutdown behavior (queue/writer).
- Concurrency tests + integrity verification stress tests.

### 🛣️ V0.0.4 — Observability & Performance Gates
- Health checks (writer/store).
- Metrics & diagnostics (queue depth, retry count, write latency).
- **Benchmark suite** integrated in CI (baseline + regression detection).

### 🛣️ V0.0.5 — Stores & Developer Experience
- MongoDB options tuning (timeouts, write concerns, indexes).
- Optional additional store (e.g., PostgreSQL) **or** file-based append-only store for demos/tests.
- Docker compose example (API + Mongo).

### 🛣️ V0.0.6 — Protocol Hardening (Polyglot Preparation)
- Canonical serialization rules (UUID, timestamp precision, field order, hex casing).
- Official **test vectors** (input → expected hash) to guarantee cross-language compatibility.
- Spec versioning clarified.

### 🛣️ V0.0.7 — GDPR-Friendly Building Blocks
- Payload minimization helpers (redaction/whitelisting).
- Optional “payload reference” mode (PII off-ledger + hash on-ledger).
- Retention hooks.

### 🛣️ V0.0.8 — Merkle Batching (Performance & Pruning Foundations)
- Periodic Merkle root calculation (`LedgerSealerService`).
- Checkpoints that accelerate verification.
- Batch integrity proofs.

### 🛣️ V0.9.0 — Public Beta (LinkedIn / Feedback Release)
Goal: **API stability + excellent docs + real-world feedback**.
- Freeze public API surface (breaking changes minimized).
- Samples (Minimal API / MVC / Worker).
- Migration guide (if needed).
- Issue templates + contribution guide tuned for community input.

### 🚀 V1.0.0 — Stable Release
- Strong stability guarantees (backwards compatibility policy).
- Comprehensive integration + perf tests in CI.
- Clear threat model + operational guidance.
- Optional anti-deletion strategy (anchoring and/or immutable storage integration, if included by then).

---

<a id="after-v1"></a>
## 🧩 After V1.0.0 (V2.0.0+)

Some problems are genuinely hard and often require a major version to evolve safely:

- Distributed writers / multi-instance correctness (single chain head)
- Strict non-blocking without data loss (durable outbox + replay)
- Cross-language byte-for-byte canonicalization (payload edge cases)
- GDPR right-to-erasure without losing auditability (termination/rebasing)
- Anchoring backends standardization (avoid vendor lock-in)

---

<a id="when-v1"></a>
## 🗓️ When should you publish V1.0.0? (Versioning Guidance)

Publish **V1.0.0** when you’re ready to make a stability promise to your users:

- Public API is stable (you can support it without breaking changes for a while)
- Protocol spec is stable (canonical serialization + test vectors included)
- Integration tests (store/writer/verification) + performance baselines are solid
- README matches reality (guarantees + limitations + threat model)
- Beta feedback (0.9.x) is incorporated and migration is clear

Rule of thumb:
- **0.x** = you can still break APIs freely
- **1.0.0** = you commit to stability and predictable upgrades

---

<a id="contributing"></a>
## 💻 Contribution and Development

PROVANCE is an open-source, polyglot protocol designed for maximum language compatibility.  
All contributors must follow the cryptographic and data structure rules defined in `PROVANCE_SPEC.md`.

| Component | Language | Status | Location |
| :--- | :--- | :--- | :--- |
| Provance.Core | C# | Active | src/dotnet/Provance.Core |
| Provance.AspNetCore.Middleware | C# | Active | src/dotnet/Provance.AspNetCore.Middleware |
| Provance.Storage.MongoDB | C# | Active | src/dotnet/Provance.Storage.MongoDB |
| Provance.Java | Java | Planning | src/java |
| Provance.Rust | Rust | Planning | src/rust |

---

<a id="sponsorship"></a>
## 💖 Sponsorship

The development and long-term maintenance of PROVANCE are only possible with the support of the community and our corporate users.  
If PROVANCE is critical to your organization's security or compliance posture, please consider sponsoring this project:
https://github.com/sponsors/h-ibrahim365

---

<a id="license"></a>
## 📝 License

MIT — see [LICENSE.md](LICENSE.md).