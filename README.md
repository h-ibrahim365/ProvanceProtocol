# 🔱 PROVANCE Protocol : The Bulletproof Data Ledger

**Cryptographically guarantee the integrity of your application’s audit logs.**

[![Sponsor](https://img.shields.io/badge/sponsor-30363D?style=for-the-badge&logo=GitHub-Sponsors&logoColor=#white)](https://github.com/sponsors/h-ibrahim365)
[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen)](https://github.com/h-ibrahim365/ProvanceProtocol/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.md)

| Package | Status | Version |
| :--- | :--- | :--- |
| **C# (NuGet)** | Active | [![Nuget Version](https://img.shields.io/nuget/v/Provance.Core.svg?style=flat)](https://www.nuget.org/packages/Provance.Core) |
| **Java (Maven)** | Planning | [![Maven Central](https://img.shields.io/badge/Maven-Soon-orange)](https://search.maven.org/)|
| **Rust (Cargo)** | Planning | [![Crates.io](https://img.shields.io/badge/Cargo-Soon-red)](https://crates.io/)|

***
## ⚡️ The Problem: Logs Lie. The Solution: Cryptography.

In enterprise and compliance-driven applications, standard audit logs are mutable and can be easily deleted or modified by an attacker. This compromises security and regulatory compliance (GDPR, ISO 27001).

**PROVANCE** solves this fundamental trust issue. It transforms your traditional log store into a **mathematically sealed, tamper-evident ledger** by applying cryptographic hash-chaining at the application layer. This provides an unassailable **"Chain of Custody"** for every system event.

***
## 🚀 Core Features — V0.0.1 Engineered for Resilience

PROVANCE is designed not only for integrity but also for **non-blocking speed** and massive scalability.

* **🛡️ Tamper-Evident Hashing:** Every log entry is sealed by calculating a `CurrentHash` based on its data and the `PreviousHash`. Modifying even a single past record **instantly invalidates the entire chain**, triggering a clear anomaly alert.
* **⚡️ Zero-Blocking Asynchronicity:** Logging is delegated via a high-performance `System.Threading.Channels` to a dedicated background service (`LedgerWriterService`). The main request thread is **never blocked** by computationally heavy database I/O or SHA-256 calculation, ensuring maximum API throughput.
* **✅ Verifiable Integrity:** The `VerifyChainIntegrityAsync` method allows for a fast and complete cryptographic verification of the ledger's entire history, from the newest entry back to the Genesis Hash.

***
## 📈 Product Roadmap

This roadmap outlines the path to full enterprise readiness.

### 🛣️ V0.0.2: Production Readiness (Data Layer)
The focus shifts from core logic to persistent reliability.
* **Feature:** Implement a production-ready `ILedgerStore` based on a secure NoSQL database (e.g., MongoDB, intended for persistence and scalability).
* **Feature:** Implement automatic error handling and retry logic within the `LedgerWriterService` for database connection failures.

### 🛣️ V0.0.3: Data Pruning & Archival
Introduction of cryptographic summarization to manage storage size without compromising integrity.
* **Feature:** Implement the `LedgerSealerService` for periodic Merkle Tree root calculation.
* **Feature:** Introduce `Merkle Tree Archival` to safely summarize old log batches into a verifiable root hash, allowing raw data pruning.

### 🚀 V1.0.0: Enterprise Compliance & External Trust
Achieving stability and adding the advanced features required for regulatory compliance and external auditing.
* **Feature:** Implement **Audit-Proof Rebasing** and **Termination Entries** (GDPR Right to Erasure mechanism).
* **Feature:** Implement **External Anchoring** to publish periodic Root Hashes to a verifiable, external service.
* **Stability:** Full coverage of integration and performance tests.

***
## 🛠️ Quick Start (C# / .NET)

This guide covers the core integration for an ASP.NET Core application.

### 1. Installation

    # Install the core logging and middleware packages
    dotnet add package Provance.Core
    dotnet add package Provance.AspNetCore.Middleware

### 2. Configure Services (Program.cs)

Register the core services and the background processing hosts.

    // 1. Initialize Provance Core Ledger
    builder.Services.AddProvanceLogging(options =>
    {
        // The Genesis hash is the immutable starting point of your ledger.
        // **MUST BE HARDCODED AND NEVER CHANGED AFTER DEPLOYMENT**
        options.GenesisHash = "GENESIS_ROOT_HASH_0000000000000000000000000000000000000000000000000000000000000000";
    });

    // 2. Register the non-blocking background services
    // LedgerWriterService: Consumes entries and writes them to the persistent store.
    builder.Services.AddHostedService<LedgerWriterService>();

    // LedgerSealerService (Placeholder for future Merkle Tree logic)
    // builder.Services.AddHostedService<LedgerSealerService>();

### 3. Apply Request Middleware

Add the middleware high in your pipeline to ensure all incoming requests are tracked before any business logic executes.

    // Use the Provance Logger middleware
    app.UseProvanceLogger();

    // Continue with standard application setup
    app.MapControllers();

***
## 💻 Contribution and Development

PROVANCE is an open-source, polyglot protocol designed for maximum language compatibility. All contributors must follow the core cryptographic and data structure rules defined in the PROVANCE_SPEC.md.

| Component | Language | Status | Location |
| :--- | :--- | :--- | :--- |
| Provance.Core | C# | Active | src/dotnet/Provance.Core |
| Provance.AspNetCore.Middleware | C# | Active | src/dotnet/Provance.AspNetCore.Middleware |
| Provance.Java | Java | Planning | src/java |
| Provance.Rust | Rust | Planning | src/rust |

***
## 💖 Sponsorship

The development and long-term maintenance of PROVANCE are only possible with the support of the community and our corporate users. If PROVANCE is critical to your organization's security or compliance posture, please consider sponsoring this project.

***
## 📝 License

PROVANCE Protocol is licensed under the MIT License. See the LICENSE.md file for details.