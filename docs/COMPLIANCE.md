# Compliance mapping

PROVANCE is a library. Regulations such as NIS2, DORA or the GDPR apply to **organizations**, not to libraries, so
no library is "NIS2 compliant" or "DORA certified". What a library can do is meet specific technical controls that
those regulations and standards require. This page lists, for each framework, the controls PROVANCE helps you meet,
how, and what remains your responsibility.

Use it to answer a security questionnaire, to prepare an audit, or to decide whether PROVANCE fits your
requirements. Every reference links to its source at the end of the page.

> This page is engineering documentation, not legal advice or an audit opinion. Whether your organization complies
> with a framework is decided by your own assessment, your auditor or your regulator.

## Legend

| | Meaning |
| :-- | :------ |
| ✅ | **Covered**: PROVANCE does this, with the default setup or a documented option. |
| 🟡 | **Partial**: PROVANCE provides part of it; the rest depends on your infrastructure or process. |
| 🔜 | **Planned for 0.3**: on the roadmap (ECDSA signatures, external anchoring, health checks). Not a commitment. |
| ⬜ | **Your responsibility**: outside what a library can do. Listed so nothing is forgotten. |

## What PROVANCE provides

The tables below refer to these capabilities.

| Capability | What it does | Since |
| :--------- | :----------- | :---- |
| **Tamper evidence** | Every entry is chained to the previous one with SHA-256 and signed with HMAC-SHA256. Verification finds any entry edited, deleted, inserted or reordered, and says which one. | 0.1 |
| **Checkpoints** | Signed statements "the ledger was at entry N with hash H", published automatically to storage you choose. They detect entries removed from the end and full rewrites by someone holding the key. | 0.1 |
| **Key rotation** | Retired keys stay usable for verification. | 0.1 |
| **Structured, UTC entries** | Every entry has a sequence, a UTC timestamp, an event type and a canonical JSON payload (actor, data). No free text concatenation, so no log injection through the format. | 0.1 |
| **Independent verification** | Same format in .NET, Java and Python, published specification and test vectors. The `provance verify` command checks a ledger without the application. | 0.1 |
| **Recorded erasure** | A payload can be erased (GDPR) while the entry and the chain remain. Since 0.2 each erasure leaves a signed record, and a payload removed directly in the database is reported. | 0.1, 0.2 |
| **Archive copy** | Every entry is copied, after being checked, to a second database where the application can only insert. | 0.2 |
| **Field-level comparison** | Shows which field of which entry was changed, with the original and the current value, and which copy is the original. | 0.2 |
| **Retention** | Old entries leave the primary store once the archive holds them, after a signed retention record; the ledger still verifies. | 0.2 |
| **ECDSA signatures** | Verification with a public key only: auditors and third parties can verify without being able to sign. | 🔜 0.3 |
| **External anchoring** | Checkpoints sent to immutable storage (S3 Object Lock, Azure immutable blobs) and to RFC 3161 timestamping authorities, read back at verification. | 🔜 0.3 |
| **Health and metrics** | Age of the last anchored checkpoint, archive lag, anchoring failures, as health checks and OpenTelemetry metrics. | 🔜 0.3 |

What PROVANCE does **not** do, whatever the framework: prevent someone with database access from changing the
table (it **detects** it; preventing it is a matter of database permissions and immutable storage), manage your
keys (use a secret store or a KMS), synchronize clocks (NTP is part of your infrastructure), decide what to log,
alert or review logs (your SIEM or monitoring), or report incidents to an authority.

## At a glance

| Framework | Applies to | Where PROVANCE helps |
| :-------- | :--------- | :------------------- |
| [NIS2](#nis2) | Essential and important entities in the EU | Log integrity, retention, redundancy |
| [DORA](#dora) | EU financial entities and their ICT providers | Log integrity, retention |
| [GDPR](#gdpr) | Anyone processing personal data of people in the EU | Integrity, erasure without losing evidence, storage limitation |
| [EU AI Act](#eu-ai-act) | Providers and deployers of high-risk AI systems | Automatic, tamper-evident event logs over the system's lifetime |
| [Cyber Resilience Act](#cyber-resilience-act) | Manufacturers of products with digital elements sold in the EU | Security logging and data integrity in your product |
| [eIDAS](#eidas) | Anyone relying on electronic time stamps in the EU | Qualified time stamps on checkpoints (0.3) |
| [ISO/IEC 27001:2022](#isoiec-270012022) | Organizations certified or certifying | Annex A 8.15 logging, log protection techniques named by ISO 27002 |
| [SOC 2](#soc-2) | Service organizations reporting to customers | Evidence for monitoring and integrity criteria |
| [NIST SP 800-53 Rev. 5](#nist-sp-800-53-rev-5) | US federal systems, FedRAMP, many contracts | AU-9, AU-9(2), AU-9(3), AU-10, AU-11 |
| [NIST CSF 2.0](#nist-csf-20) | Voluntary, widely used | PR.PS-04, data integrity |
| [CIS Controls v8](#cis-controls-v8) | Voluntary, widely used | Control 8, audit log management |
| [OWASP ASVS 5.0](#owasp-asvs-50) | Application security requirements | V16.2 and V16.4, log content and protection |
| [PCI DSS v4.0.1](#pci-dss-v401) | Anyone storing, processing or transmitting card data | Requirement 10.3.4 (change detection on audit logs), 10.3, 10.5 |
| [HIPAA Security Rule](#hipaa-security-rule) | US covered entities and business associates | Audit controls, integrity, authentication of ePHI |
| [21 CFR Part 11 and EU GMP Annex 11](#21-cfr-part-11-and-eu-gmp-annex-11) | Pharma, biotech, medical devices (GxP) | Computer-generated, time-stamped audit trails; ALCOA+ |
| [SEC Rule 17a-4](#sec-rule-17a-4) | US broker-dealers | Audit-trail alternative to WORM storage |
| [MiFID II](#mifid-ii) | EU investment firms | Records that cannot be manipulated, corrections visible with the prior content |
| [NYDFS Part 500](#nydfs-part-500) | Financial services regulated in New York | Audit trails to detect cybersecurity events, five-year records |
| [SWIFT CSCF](#swift-cscf) | SWIFT users | Control 6.4, logging and monitoring |
| [European Health Data Space](#european-health-data-space) | EHR system manufacturers, health providers in the EU | Logs of who accessed health data |
| [ICH E6(R3)](#ich-e6r3) | Clinical trials | Audit trails that record every change, cannot be disabled, UTC timestamps |
| [ISO/IEC 42001](#isoiec-42001) | AI management systems | A.6.2.8 AI system recording of event logs |
| [IEC 62443 and CLC/TS 50701](#iec-62443-and-clcts-50701) | Industrial control systems, railways | SR 2.8 to 2.12, SR 3.9, SR 6.1 |
| [NIST SP 800-171 and CMMC](#nist-sp-800-171-and-cmmc) | US defense contractors, controlled unclassified information | 03.03.08 audit record protection |
| [CSA Cloud Controls Matrix](#csa-cloud-controls-matrix) | Cloud providers and customers, CSA STAR | LOG-02 audit logs protection, LOG-09 log protection |
| [OWASP Top 10:2025](#owasp-top-102025) | Web applications | A09 Security Logging and Alerting Failures |
| [MITRE ATT&CK](#mitre-attck) | Detection engineering | T1070 Indicator Removal, T1565.001 Stored Data Manipulation |
| [Belgium: CyberFundamentals](#belgium-cyberfundamentals) | Belgian NIS2 entities and others | PR.PT-1 audit log records |
| [France: CNIL, ANSSI, cash register law](#france) | Organizations in France | Logging recommendations, unalterable cash register data |
| [Germany: GoBD, BSI](#germany) | Bookkeeping in Germany, German public sector and cloud | Immutability of electronic records, logging module |
| [United Kingdom: NCSC CAF](#united-kingdom-ncsc-caf) | UK operators of essential services, NHS | C1.b Securing logs |
| [Australia: ISM](#australia-ism) | Australian government and suppliers | ISM-1815, ISM-1988, ISM-1405 |

## EU regulations

### NIS2

Directive (EU) 2022/2555, Article 21(2)(b) (incident handling), detailed for digital infrastructure and digital
service providers by Commission Implementing Regulation (EU) 2024/2690, Annex, section 3.2 (monitoring and
logging). For other entities, section 3.2 and the ENISA implementation guidance are the usual reference. In
Belgium, the transposing law is supervised by the Centre for Cybersecurity Belgium (CCB).

| Requirement (CIR 2024/2690, section 3.2) | | How |
| :--------------------------------------- | :-: | :-- |
| Logs protected against unauthorized access and changes | ✅ / ⬜ | Changes and deletions are **detected** (tamper evidence, checkpoints, archive). Preventing access is done with database permissions. |
| Predefined retention period | ✅ | Retention (0.2), from a period you set. |
| Logs redundant; monitoring independent of the monitored systems | 🟡 → 🔜 | Archive copy in another database (0.2). Anchoring to storage outside your control (0.3). |
| Logging of the activation, stopping or pausing of logging (3.2.3(k)) | 🔜 | Today the ledger cannot know that the application stopped writing. 0.3: missing anchored checkpoints and health checks reveal it. |
| Synchronized time across systems | ⬜ / 🔜 | Entries use the server clock in UTC: synchronize it with NTP. 0.3: RFC 3161 time stamps give an independent time reference for checkpoints. |
| Incident handling, reporting to the CSIRT, risk management, governance | ⬜ | Organizational measures. PROVANCE provides integrity-checked evidence for investigations. |

### DORA

Regulation (EU) 2022/2554 (Digital Operational Resilience Act), applicable since 17 January 2025 to EU financial
entities. Logging is detailed in Commission Delegated Regulation (EU) 2024/1774 (ICT risk management framework),
Article 12.

| Requirement (RTS 2024/1774, Article 12) | | How |
| :-------------------------------------- | :-: | :-- |
| Protect logging systems and log information against tampering, deletion and unauthorized access | ✅ / ⬜ | Tampering and deletion detected (tamper evidence, checkpoints, archive, retention records). Access control is yours. |
| Retention period based on business and security objectives and the risk assessment | ✅ | Retention (0.2), with the period you decide. |
| Clocks synchronized to a documented reliable reference time source | ⬜ / 🔜 | NTP on your servers. 0.3: RFC 3161 time stamps from a timestamping authority. |
| Detect failures of the logging systems | 🔜 | 0.3 health checks and metrics: last anchored checkpoint age, archive lag, anchoring failures. |
| ICT third-party risk (when PROVANCE is part of your stack) | 🟡 | Open source (MIT), no service dependency, published specification, test vectors and changelog, security policy. |

### GDPR

Regulation (EU) 2016/679. Audit logs usually contain personal data (user ids, actions), so they are themselves
subject to the GDPR. PROVANCE is designed for that.

| Article | | How |
| :------ | :-: | :-- |
| Art. 5(1)(f) integrity, Art. 32(1)(b) integrity of processing systems | ✅ | Tamper evidence on every entry; verification on demand or scheduled. |
| Art. 17 right to erasure | ✅ | Payload erasure keeps the entry, its type, its time and its hash: the evidence that something happened remains, the personal data does not. Since 0.2 the erasure itself is recorded and signed. |
| Art. 5(1)(e) storage limitation | ✅ | Retention removes old entries from the primary store (0.2). Set the archive's own retention according to your policy. |
| Art. 5(1)(c) data minimization | ⬜ | You decide what goes in the payload. The HTTP integrations never record bodies or headers, and leave out query strings, IP addresses and user agents by default. |
| Art. 5(2) and 24 accountability | 🟡 | Verification results and checkpoints are evidence you can show. Your records of processing remain yours. |
| Art. 33 breach investigation | 🟡 | Field-level comparison shows what was changed and the original value (0.2). |

### EU AI Act

Regulation (EU) 2024/1689. Article 12 requires high-risk AI systems to allow the automatic recording of events
(logs) over their lifetime. Article 19 (providers) and Article 26(6) (deployers) require keeping those logs for a
period appropriate to the purpose, of at least six months unless other law says otherwise. After the Digital
Omnibus on AI, the high-risk rules apply from 2 December 2027 (Annex III systems) and 2 August 2028 (Annex I).

| Requirement | | How |
| :---------- | :-: | :-- |
| Automatic recording of events over the lifetime of the system (Art. 12(1)) | ✅ | Record each inference, decision, human override or input reference as a ledger entry. |
| Logs that can be relied on in an investigation | ✅ | Tamper evidence and checkpoints: a log edited after an incident is detected. |
| Retention of at least six months (Art. 19(1), Art. 26(6)) | ✅ | Retention with a period of six months or more, archive for the full history. |
| Logs available to authorities and verifiable by them | ✅ → 🔜 | JSON Lines export and `provance verify`. 0.3: ECDSA so a third party verifies without the secret key. |
| What to log for your system, traceability design, human oversight | ⬜ | Your system design and risk management. |

### Cyber Resilience Act

Regulation (EU) 2024/2847. If you **manufacture** a product with digital elements sold in the EU, Annex I, Part I,
point 2 sets essential requirements. Reporting obligations apply from 11 September 2026 and most obligations from
11 December 2027.

| Requirement (Annex I, Part I) | | How |
| :---------------------------- | :-: | :-- |
| (2)(l) Record and monitor relevant internal activity, including access to or modification of data, services or functions | ✅ | PROVANCE is the recording part: tamper-evident entries for those events. The user opt-out the CRA mentions is a setting of your product. |
| (2)(f) Protect the integrity of stored data and report corruption | ✅ | For the audit records themselves: verification reports exactly which entry is corrupted. |
| (2)(m) Let users remove their data | ✅ | Recorded erasure removes payloads without breaking the audit trail. |

PROVANCE itself is free and open source software published by an individual, which the Commission's guidance places
outside the CRA. Its supply chain practices are listed in [The project itself](#the-project-itself).

### eIDAS

Regulation (EU) No 910/2014 as amended by Regulation (EU) 2024/1183. Under Article 41, a qualified electronic time
stamp enjoys the presumption of the accuracy of its date and time and of the integrity of the data it is bound to.

| | | How |
| :-- | :-: | :-- |
| Proving that the ledger was in a given state at a given time, with legal weight | 🔜 | 0.3: checkpoints time-stamped through RFC 3161. With a qualified trust service provider from the EU Trusted Lists, the time stamp is a qualified electronic time stamp. |

### MiFID II

Directive 2014/65/EU and Commission Delegated Regulation (EU) 2017/565, Article 72 (records kept by investment
firms).

| Requirement (Delegated Regulation 2017/565, Article 72(1)) | | How |
| :--------------------------------------------------------- | :-: | :-- |
| (a) The authority can access the records and reconstruct each key stage of the processing of each transaction | ✅ / ⬜ | One entry per stage, in sequence; JSON Lines export. What you record per stage is yours. |
| (b) Any correction or amendment, and the content of the records before it, can be easily ascertained | ✅ | A correction is a new entry; the previous entries stay. A change made directly in the table is detected, and the field-level comparison shows the content before it. |
| (c) The records cannot otherwise be manipulated or altered | ✅ / ⬜ | Manipulation is detected; preventing it is done with database permissions and, in 0.3, immutable storage anchors. |

### European Health Data Space

Regulation (EU) 2025/327, applying in stages over the coming years. Electronic health record (EHR) systems must
log access to personal electronic health data (who, when, which data), and patients get the right to see who
accessed their data.

| Requirement | | How |
| :---------- | :-: | :-- |
| EHR systems record who accessed which electronic health data, and when | ✅ / ⬜ | One entry per access with the professional, the category of data and the time. |
| Access logs that patients and authorities can trust | ✅ | Tamper evidence: an access entry removed or changed after the fact is detected. |
| Tools to review and analyse the log data | 🟡 | Export, comparison and verification; analysis is your application. |

## National frameworks

### Belgium: CyberFundamentals

The CyberFundamentals (CyFun) framework of the Centre for Cybersecurity Belgium is one of the ways for Belgian
entities to show NIS2 conformity. In the 2023 version, PR.PT-1 asks that audit and log records are determined,
documented, implemented and reviewed in accordance with policy.

| Measure | | How |
| :------ | :-: | :-- |
| PR.PT-1 Audit and log records implemented and reviewed | ✅ / ⬜ | Recording, retention and verification; the policy and the review are yours. |

### France

* **CNIL, recommendation on logging** (deliberation n° 2021-122): log access and actions on personal data, keep
  the logs six months to one year (up to three years when justified), protect them against misuse and analyse them
  automatically. PROVANCE: ✅ recording, ✅ retention with your period, ✅ protection against undetected changes,
  ⬜ analysis.
* **ANSSI, security recommendations for a logging system architecture**: timestamp every event (R3), standardise
  timestamps and synchronise clocks (R4, R5), protect logs in transit (R17), define retention (R24, R25), restrict
  who can modify or delete logs (R26). PROVANCE: ✅ R3 and the UTC format of R4, ✅ R24 and R25 with retention,
  ✅ detection of what R26 restricts, ⬜ clock synchronisation and transport security.
* **Certified cash register software** (article 286, I, 3° bis of the Code général des impôts): payment data must be
  unalterable, secured, kept and archived; corrections are recorded as new operations, and the original data and the
  dated history of corrections must remain visible. Chaining and electronic signatures are cited by the tax
  administration as means to secure the data. PROVANCE: ✅ unalterable history with corrections as new entries,
  ✅ chained and signed records, ✅ archive. The certification itself (NF 525 or an equivalent attestation) covers
  the whole software, not a library.

### Germany

* **GoBD** (principles for electronic bookkeeping, letter of the Federal Ministry of Finance), immutability principle
  (Unveränderbarkeit): an entry must not be changed in a way that makes its original content undeterminable,
  changes and deletions must be logged and marked as such, and immutability can be ensured by software, for example
  automatic logging and versioning. PROVANCE: ✅ an append-only, verifiable change log for bookkeeping records, with
  the actor. Paragraph numbers differ between the 2014 letter and its later amendments: check the current version.
* **BSI IT-Grundschutz, module OPS.1.1.5 Logging**, and **BSI C5** for cloud services: logging, protection of
  logging data and restricted access to logging components. PROVANCE: ✅ integrity of the logged data, ⬜ access
  and configuration of your logging infrastructure.

### United Kingdom: NCSC CAF

Cyber Assessment Framework, principle C1 Security monitoring. Used for operators of essential services and in the
NHS Data Security and Protection Toolkit.

| Contributing outcome | | How |
| :------------------- | :-: | :-- |
| C1.a Logs synchronised with an accurate common time source | ⬜ / 🔜 | NTP; 0.3 RFC 3161 time stamps. |
| **C1.b Securing logs: the integrity of log data is protected, and any modification, including deletion, is detected and attributed** | ✅ | Exactly what verification does: it names the entry that was modified or removed. |
| C1.b Analysis on copies so the master copy stays unaltered | ✅ | Archive copy and JSON Lines export. |
| C1.c Generating alerts | ⬜ | Alert on verification failures in your monitoring. |

### Australia: ISM

Information Security Manual, guidelines for system monitoring.

| Control | | How |
| :------ | :-: | :-- |
| ISM-1815 Event logs are protected from unauthorised modification and deletion | ✅ / ⬜ | Detected by verification; prevented by permissions. |
| ISM-1988 Event logs retained in a searchable manner for at least 12 months | ✅ | Retention and archive. |
| ISM-1405 Centralised event logging facility | 🟡 | Archive copy; export to your central platform. |
| ISM-0988 Accurate and consistent time source | ⬜ / 🔜 | NTP; 0.3 RFC 3161. |

## Security standards

### ISO/IEC 27001:2022

Annex A control **8.15 Logging**: logs recording activities, exceptions, faults and other relevant events should be
produced, stored, protected and analysed. ISO/IEC 27002:2022 guidance for 8.15 names techniques to protect logs
from tampering, among them **cryptographic hashing**, append-only recording, read-only recording and public
transparency files. PROVANCE implements the first two. Read-only storage comes from an insert-only archive account today and from the planned immutable storage anchors (0.3).

| Control | | How |
| :------ | :-: | :-- |
| 8.15 Logging: logs produced and stored | ✅ | Structured entries in your own database. |
| 8.15 Logging: logs protected; users cannot alter or delete their own logs undetected | ✅ | Hash chain and signatures (cryptographic hashing), append-only ledger, checkpoints. |
| 8.15 Logging: logs analysed | ⬜ | Your SIEM and review process. Verification can run on a schedule. |
| 8.17 Clock synchronization | ⬜ / 🔜 | NTP; 0.3 RFC 3161 time stamps. |
| 5.28 Collection of evidence | ✅ | Verification result, checkpoints, field-level comparison and exports are evidence whose integrity can be demonstrated. |
| 5.33 Protection of records | ✅ | Tamper evidence, archive copy, retention. |
| 5.34 Privacy and protection of PII | ✅ | Recorded erasure, minimal HTTP payloads by default. |

### SOC 2

AICPA Trust Services Criteria. SOC 2 does not prescribe controls; PROVANCE supplies evidence for criteria that
auditors commonly test with logs.

| Criterion | | How |
| :-------- | :-: | :-- |
| CC7.2 Monitoring of system components for anomalies | 🟡 | Integrity-verified audit trail and scheduled verification; alerting stays in your monitoring. |
| CC7.3 Evaluation of security events | 🟡 | Field-level comparison tells what changed and what the original was. |
| Processing integrity and confidentiality criteria, where in scope | 🟡 | Tamper evidence on the records of processing; erasure for confidentiality commitments. |

### NIST SP 800-53 Rev. 5

Audit and Accountability (AU) family. Also the basis of FedRAMP baselines, of NIST SP 800-171 (and so CMMC) and of
the FBI CJIS Security Policy 6.0.

| Control | | How |
| :------ | :-: | :-- |
| AU-3 Content of audit records | ✅ / ⬜ | Event type, UTC time, sequence, and the actor and data you put in the payload. |
| AU-8 Time stamps | ✅ / ⬜ | UTC timestamps in a fixed format; clock source is yours. |
| AU-9 Protection of audit information (unauthorized modification and deletion) | ✅ | Detected by verification. |
| AU-9(2) Store on separate physical systems or components | ✅ | Archive copy in another database (0.2). |
| AU-9(3) Cryptographic protection | ✅ | SHA-256 hash chain and HMAC-SHA256 signatures. |
| AU-9(4) Access by subset of privileged users | ⬜ | Database permissions. Use an archive account that can only insert. |
| AU-5 Response to audit logging process failures | 🔜 | 0.3 health checks and metrics. |
| AU-10 Non-repudiation | 🟡 → 🔜 | HMAC proves integrity to whoever holds the key. 0.3 ECDSA signatures and anchoring support non-repudiation towards third parties. |
| AU-11 Audit record retention | ✅ | Retention (0.2) and archive. |

### NIST CSF 2.0

| Subcategory | | How |
| :---------- | :-: | :-- |
| PR.PS-04 Log records are generated and made available for continuous monitoring | ✅ / ⬜ | Entries are generated by the application and stored in your database; continuous monitoring is your tooling. |
| PR.DS-01 Confidentiality, integrity and availability of data at rest are protected | 🟡 | Integrity of audit data at rest is verifiable; confidentiality and availability depend on your database. |
| DE.CM Continuous monitoring | 🟡 | Scheduled verification and archive status feed your monitoring. |

### CIS Controls v8

Control 8, Audit Log Management.

| Safeguard | | How |
| :-------- | :-: | :-- |
| 8.2 Collect audit logs, 8.5 Collect detailed audit logs | ✅ / ⬜ | One entry per event or per HTTP request, with the details you choose. |
| 8.3 Ensure adequate audit log storage | ✅ | Retention keeps the primary store small; the archive keeps the history. |
| 8.4 Standardize time synchronization | ⬜ | NTP. |
| 8.9 Centralize audit logs | 🟡 | Archive copy to a separate database; export in JSON Lines for your log platform. |
| 8.10 Retain audit logs | ✅ | Retention with your period (CIS asks for at least 90 days). |
| 8.11 Conduct audit log reviews | ⬜ / ✅ | Reviews are yours; verification tells you whether what you review is intact. |

### OWASP ASVS 5.0

Chapter V16, Security Logging and Error Handling.

| Requirement | | How |
| :---------- | :-: | :-- |
| 16.2.1 Each log entry includes when, where, who, what | ✅ | Timestamp, ledger and event type, actor and data in the payload. |
| 16.2.2 Synchronized time sources; timestamps in UTC or with an explicit offset | ✅ / ⬜ | UTC timestamps; synchronization is yours. |
| 16.2.4 Logs readable by the log processor, common format | ✅ | Canonical JSON entries, JSON Lines export. |
| 16.4.1 Logging components encode data to prevent log injection | ✅ | Payloads are canonical JSON; no string concatenation into a log line. |
| 16.4.2 Logs protected from unauthorized access and cannot be modified | ✅ / ⬜ | Modification detected; access control on the table is yours. |
| 16.4.3 Logs transmitted to a logically separate system | ✅ | Archive copy (0.2); anchoring (0.3). |

### OWASP Top 10:2025

A09:2025, Security Logging and Alerting Failures, lists "the integrity of logs is not properly protected from
tampering" as a weakness, and recommends an audit trail with integrity controls to prevent tampering or deletion,
such as append-only tables. PROVANCE is such an audit trail. ✅

### ISO/IEC 42001

AI management system standard. Annex A control **A.6.2.8, AI system recording of event logs**: the organization
determines at which phases of the AI system life cycle event logging is enabled. PROVANCE records those events in
a tamper-evident way. ✅ Recording and integrity. ⬜ Choice of phases and events. Pairs with the
[EU AI Act](#eu-ai-act) record-keeping duties.

### IEC 62443 and CLC/TS 50701

IEC 62443-3-3, system security requirements for industrial automation and control systems. CLC/TS 50701 applies
the IEC 62443 approach to railway applications.

| Requirement | | How |
| :---------- | :-: | :-- |
| SR 2.8 Auditable events | ✅ / ⬜ | Record the events your security level requires. |
| SR 2.9 Audit storage capacity | ✅ | Retention keeps the primary store bounded. |
| SR 2.10 Response to audit processing failures | 🔜 | 0.3 health checks and metrics. |
| SR 2.11 Timestamps | ✅ / ⬜ | UTC timestamps; time source is yours. |
| SR 2.12 Non-repudiation | 🟡 → 🔜 | Integrity with HMAC today; ECDSA and anchoring in 0.3. |
| SR 3.9 Protection of audit information | ✅ / ⬜ | Unauthorized modification and deletion detected; access control is yours. |
| SR 6.1 Audit log accessibility | ✅ | Read access through the stores, export, CLI. |
| SR 6.2 Continuous monitoring | 🟡 | Scheduled verification feeds your monitoring. |

### NIST SP 800-171 and CMMC

NIST SP 800-171 Rev. 3, family 03.03 Audit and Accountability. CMMC level 2 assesses the same requirements
(AU.L2-3.3.x, numbered from Rev. 2).

| Requirement | | How |
| :---------- | :-: | :-- |
| 03.03.01 Event logging, 03.03.02 Audit record content, 03.03.03 Audit record generation | ✅ / ⬜ | Structured entries; the events and content are yours. |
| 03.03.04 Audit logging process failure response | 🔜 | 0.3 health checks and metrics. |
| 03.03.07 Time stamps | ✅ / ⬜ | UTC timestamps; authoritative time source is yours. |
| 03.03.08 Audit record protection (unauthorized access, modification, deletion) | ✅ / ⬜ | Modification and deletion detected; access control is yours. |

### CSA Cloud Controls Matrix

CSA CCM v4, Logging and Monitoring domain, also used for CSA STAR.

| Control | | How |
| :------ | :-: | :-- |
| LOG-02 Audit logs protection (protect and retain audit logs) | ✅ | Tamper evidence and retention. |
| LOG-09 Log protection | ✅ / ⬜ | Integrity detected; access control is yours. |

### MITRE ATT&CK

For detection engineering: PROVANCE detects these techniques when they target the ledger.

| Technique | | How |
| :-------- | :-: | :-- |
| T1070 Indicator Removal (deleting or altering logs to hide activity) | ✅ | Deleted, inserted or edited entries fail verification; removed entries at the end are caught by checkpoints. |
| T1565.001 Stored Data Manipulation | ✅ | Edited entries fail verification; the field-level comparison shows the original value. |

## Sector rules

### PCI DSS v4.0.1

Requirement 10, Log and monitor all access to system components and cardholder data.

| Requirement | | How |
| :---------- | :-: | :-- |
| 10.2.1 Audit logs enabled and active | ✅ / ⬜ | Record the events in scope; the HTTP integrations log every request. |
| 10.2.2 Logs record user, event type, date and time, success or failure, origin, affected resource | ✅ / ⬜ | Event type and time in every entry; the HTTP payload has actor, status, outcome, path. |
| 10.3.2 Audit log files protected to prevent modifications by individuals | ✅ / ⬜ | Modifications detected; database permissions prevent them. |
| 10.3.3 Promptly backed up to a secure, central server or media that is difficult to modify | ✅ → 🔜 | Archive copy within seconds (0.2); immutable storage anchors (0.3). |
| **10.3.4 File integrity monitoring or change-detection mechanisms on audit logs, so existing data cannot be changed without generating alerts** | ✅ | This is what PROVANCE is. Run verification on a schedule (or `provance verify` in a job) and alert on failure. |
| 10.5.1 Retain audit log history for at least 12 months, three months immediately available | ✅ | Retention of three months or more in the primary store, 12 months or more in the archive. |
| 10.6.1 System clocks synchronized | ⬜ | NTP. |
| 10.7.2 Failures of critical security controls, including audit logging mechanisms, detected and alerted | 🔜 | 0.3 health checks and metrics. |

### HIPAA Security Rule

45 CFR 164.312, technical safeguards.

| Standard | | How |
| :------- | :-: | :-- |
| 164.312(b) Audit controls: mechanisms that record and examine activity in systems with ePHI | ✅ | Recording and verification. |
| 164.312(c)(1) Integrity: protect ePHI from improper alteration or destruction | 🟡 | For audit records and anything you store in the ledger. Your clinical data has its own controls. |
| 164.312(c)(2) Mechanism to corroborate that ePHI has not been altered or destroyed in an unauthorized manner | ✅ | Verification is that mechanism, for what the ledger holds. |
| 164.316(b)(2) Retain documentation six years | 🟡 | Archive with a matching retention. |

Do not put ePHI in payloads unless you need to; when you do, erasure is available.

### 21 CFR Part 11 and EU GMP Annex 11

For electronic records in pharmaceutical, biotech and medical device manufacturing (GxP).

| Requirement | | How |
| :---------- | :-: | :-- |
| 21 CFR 11.10(e): secure, computer-generated, time-stamped audit trails that independently record the date and time of entries and actions that create, modify or delete records | ✅ | One entry per create, modify or delete, written by the application, time-stamped, chained and signed. |
| 11.10(e): record changes shall not obscure previously recorded information | ✅ | Append-only: a change is a new entry; earlier entries stay, and editing one is detected. |
| 11.10(e): audit trail retained at least as long as the records | ✅ | Archive and retention set to your record retention. |
| Annex 11, 9: record of GMP-relevant changes and deletions, with reasons, regularly reviewed | ✅ / ⬜ | Put the reason in the payload. Review is your procedure. |
| Annex 11, 12.4: record the identity of operators and the date and time | ✅ / ⬜ | Actor id in the payload, time in every entry. |
| Annex 11, 7.1 and 17: data secured, archived data checked for integrity | ✅ | Archive copy, verification of the archive like any ledger. |
| ALCOA+ (attributable, legible, contemporaneous, original, accurate, complete, consistent, enduring, available) | ✅ / ⬜ | Attributable (actor), contemporaneous (written at the time of the action), original (tamper evidence), complete (no gaps, checkpoints), consistent (sequence), enduring (archive). |
| Computer system validation | ⬜ | Validate your system; the specification and the test vectors help. |

GxP records are usually not erased: do not use payload erasure on them unless your procedures allow it.

### SEC Rule 17a-4

Since the 2022 amendments (compliance date 3 May 2023), a broker-dealer's electronic recordkeeping system can use
an audit-trail alternative to WORM storage: a complete, time-stamped audit trail of every modification and deletion,
with the date, time and identity of who made them, and the ability to re-create the original record.

| | | How |
| :-- | :-: | :-- |
| Complete, time-stamped audit trail of modifications and deletions, with the individuals | ✅ / ⬜ | One entry per change with the actor; integrity verified. |
| Re-create the original record and interim versions | ⬜ / 🟡 | Store the before and after values in the payload; field-level comparison shows them for tampering. |
| WORM storage | 🔜 | 0.3 anchoring to S3 Object Lock in compliance mode. |

### NYDFS Part 500

23 NYCRR 500.6, Audit trail, for financial services companies regulated by the New York Department of Financial
Services.

| Requirement | | How |
| :---------- | :-: | :-- |
| 500.6(a)(1) Systems designed to reconstruct material financial transactions | ✅ / ⬜ | One entry per step of the transaction; what you record is yours. |
| 500.6(a)(2) Audit trails designed to detect and respond to cybersecurity events | ✅ | An audit trail whose own tampering is detected. |
| 500.6(b) Keep these records for at least five years | ✅ | Archive with a five-year retention or more. |

### SWIFT CSCF

SWIFT Customer Security Controls Framework, control **6.4 Logging and monitoring**: record security events and
forward them to a protected log store. PROVANCE: ✅ a protected, verifiable store for the events you record on the
SWIFT-related systems, 🟡 monitoring through your SIEM.

### ICH E6(R3)

Good Clinical Practice, Annex 1, section 4 (data governance), for computerised systems in clinical trials.

| Requirement | | How |
| :---------- | :-: | :-- |
| 4.2.2(a)(ii) The initial entry and every later change or deletion are recorded, with the reason where appropriate | ✅ / ⬜ | One entry per change, reason in the payload. |
| 4.2.2(a)(i) Logs of user accounts, roles, permissions and access | ✅ / ⬜ | Record these events in the ledger. |
| 4.2.2(b) Audit trails are not disabled or modified, except in rare documented cases such as personal data entered by mistake | ✅ | Append-only and tamper-evident; recorded erasure for the rare personal data case, which leaves a signed, justified trace. |
| 4.2.2(c) Audit trails are interpretable and reviewable | ✅ | Structured entries, export, field-level comparison. |
| 4.2.2(d) Unambiguous date and time, for example UTC | ✅ | Every timestamp is UTC with an explicit `Z`. |
| 4.2.3 Planned, risk-based review | ⬜ | Your procedure; verification tells you the trail you review is intact. |

## What stays your responsibility

Across all frameworks, plan for:

* **Key management**: keep the signing key in a secret store or a KMS, never in the database; rotate it.
* **Access control**: the application account should not be able to delete the archive; the archive account
  should only insert (and update payloads for erasure).
* **Checkpoints out of reach**: store them where the people who can change the database cannot (another team,
  immutable storage). Until 0.3, a file sink on a separate system or your own `LedgerCheckpointSink`.
* **Scheduled verification and alerting**: run verification regularly and alert on failure.
* **Clock synchronization**: NTP on every server.
* **What to log**: the events your framework requires, with the actor and the reason when needed.
* **Backups**: PROVANCE detects loss; it does not restore data.

## The project itself

| Practice | Status |
| :------- | :----- |
| Open source, MIT license, public code and history | ✅ |
| Published format specification and test vectors, three independent implementations | ✅ |
| Security policy and private vulnerability reporting ([SECURITY.md](../SECURITY.md)) | ✅ |
| External security review, findings fixed and documented in the changelog | ✅ |
| Releases built in CI from tagged sources; Maven artifacts signed with GPG; PyPI and NuGet through Trusted Publishing | ✅ |
| Standard algorithms only: SHA-256, HMAC-SHA256, RFC 8785 canonical JSON (ECDSA P-256 planned). They are FIPS-approved algorithms; PROVANCE is not a FIPS 140 validated module. | ✅ |
| OpenSSF Best Practices badge, OpenSSF Scorecard, SBOM published with releases | 🔜 |

## Sources

* NIS2: [Directive (EU) 2022/2555](https://eur-lex.europa.eu/eli/dir/2022/2555/oj),
  [Implementing Regulation (EU) 2024/2690](https://eur-lex.europa.eu/eli/reg_impl/2024/2690/oj),
  [section 3.2 explained](https://nisd2.eu/en/wiki/implementation/nis2-logging),
  [ENISA implementation guidance](https://enisa.europa.eu/publications/implementation-guidance-on-nis-2-security-measures)
* DORA: [Regulation (EU) 2022/2554](https://eur-lex.europa.eu/eli/reg/2022/2554/oj),
  [Delegated Regulation (EU) 2024/1774, Article 12](https://www.springlex.eu/en/packages/dora/rts-rmf-regulation/article-12/)
* GDPR: [Regulation (EU) 2016/679](https://eur-lex.europa.eu/eli/reg/2016/679/oj)
* AI Act: [Regulation (EU) 2024/1689](https://eur-lex.europa.eu/eli/reg/2024/1689/oj),
  [Article 19](https://artificialintelligenceact.eu/article/19/),
  [application dates after the Digital Omnibus](https://www.lewissilkin.com/insights/2026/07/27/the-digital-omnibus-on-ai-enters-into-force-today-102nedo)
* Cyber Resilience Act: [Regulation (EU) 2024/2847](https://eur-lex.europa.eu/eli/reg/2024/2847/oj),
  [Annex I](https://www.springlex.eu/en/packages/cra/cra-regulation/annex-1/),
  [Commission guidance](https://digital-strategy.ec.europa.eu/en/library/commission-publishes-new-guidance-support-timely-cyber-resilience-act-implementation)
* eIDAS: [Regulation (EU) No 910/2014](https://eur-lex.europa.eu/eli/reg/2014/910/oj),
  [EU Trusted Lists](https://eidas.ec.europa.eu/efda/trust-services/browse/eidas/tls)
* ISO/IEC 27001:2022 Annex A 8.15: [overview](https://www.isms.online/iso-27001/annex-a-2022/8-15-logging-2022/)
* NIST SP 800-53 Rev. 5: [publication](https://csrc.nist.gov/pubs/sp/800/53/r5/upd1/final)
* NIST CSF 2.0: [PR.PS-04](https://csf.tools/reference/nist-cybersecurity-framework/v2-0/pr/pr-ps/pr-ps-04/)
* CIS Controls v8: [Control 8](https://controls-assessment-specification.readthedocs.io/en/stable/control-8/)
* OWASP ASVS 5.0: [V16 Security Logging and Error Handling](https://github.com/OWASP/ASVS/blob/v5.0.0/5.0/en/0x25-V16-Security-Logging-and-Error-Handling.md)
* PCI DSS v4.0.1: [PCI Security Standards Council document library](https://www.pcisecuritystandards.org/document_library/),
  [Requirement 10 summary](https://learn.microsoft.com/en-us/entra/standards/pci-requirement-10)
* HIPAA: [45 CFR 164.312](https://www.law.cornell.edu/cfr/text/45/164.312)
* 21 CFR Part 11: [11.10](https://www.ecfr.gov/current/title-21/chapter-I/subchapter-A/part-11/subpart-B/section-11.10);
  EU GMP Annex 11: [text](https://health.ec.europa.eu/system/files/2016-11/annex11_01-2011_en_0.pdf)
* SEC Rule 17a-4: [2022 amendments](https://www.dechert.com/knowledge/onpoint/2022/11/-sec-adopts-amendments-to-rule-17a-4-electronic-recordkeeping-re.html)
* MiFID II: [Delegated Regulation (EU) 2017/565, Article 72](https://gesetze.legal/eu/vo_eu_2017_565/72)
* European Health Data Space: [Regulation (EU) 2025/327](https://eur-lex.europa.eu/eli/reg/2025/327/oj),
  [requirements for EHR systems](https://privacymatters.dlapiper.com/2024/07/requirements-of-ehr-systems-under-the-european-health-data-space/)
* Belgium: [CyberFundamentals PR.PT-1](https://cyberday.ai/requirement/cyfun-pr-pt-1-auditlog-records-are-determined-documented-implemented-and-reviewed-in-accordance-with-policy),
  [Centre for Cybersecurity Belgium](https://ccb.belgium.be/)
* France: [CNIL recommendation on logging](https://cnil.fr/la-cnil-publie-une-recommandation-relative-aux-mesures-de-journalisation),
  [ANSSI logging architecture guide](https://messervices.cyber.gouv.fr/guides/recommandations-de-securite-pour-larchitecture-dun-systeme-de-journalisation),
  [BOFiP BOI-TVA-DECLA-30-10-30](https://bofip.impots.gouv.fr/bofip/10691-PGP.html/identifiant%3DBOI-TVA-DECLA-30-10-30-20160803)
* Germany: [GoBD immutability explained (PSP)](https://www.psp.eu/assets/pdfs/beitrag_expertenerlaeuterungen_gobd_unveraenderbarkeit_final.pdf),
  [BSI C5](https://www.bsi.bund.de/EN/Themen/Unternehmen-und-Organisationen/Informationen-und-Empfehlungen/Empfehlungen-nach-Angriffszielen/Cloud-Computing/Kriterienkatalog-C5/C5_AktuelleVersion/C5_AktuelleVersion.html)
* United Kingdom: [NCSC CAF, principle C1](https://www.ncsc.gov.uk/collection/cyber-assessment-framework/caf-objective-c-detecting-cyber-security-events/principle-c1-security-monitoring)
* Australia: [ISM, guidelines for system monitoring](https://www.cyber.gov.au/business-government/asds-cyber-security-frameworks/ism/cyber-security-guidelines/guidelines-for-system-monitoring)
* OWASP Top 10:2025: [A09](https://top10.owasp.org/2025/A09_2025-Security_Logging_and_Alerting_Failures/)
* ISO/IEC 42001: [A.6.2.8](https://www.isms.online/iso-42001/annex-a-controls/a-6-ai-system-life-cycle/a-6-2-8-ai-system-recording-of-event-logs/)
* IEC 62443-3-3: [table of contents (ISA)](https://www.isa.org/getmedia/4379f3a1-2687-4a0a-9d0f-20d743233c1b/ISA-62443-3-3_TOC.pdf);
  CLC/TS 50701: [CEN-CENELEC](https://www.cencenelec.eu/news-events/news/2021/eninthespotlight/2021-06-10-new-clc-ts-50701-railways-cybersecurity/)
* NIST SP 800-171 Rev. 3: [family 03.03](https://myctrl.tools/frameworks/nist-800-171-r3/family/03.03),
  [publication](https://csrc.nist.gov/pubs/sp/800/171/r3/final)
* CSA CCM: [LOG-02](https://plerion.com/cloud-knowledge-base/audit-logs-protection),
  [Cloud Controls Matrix](https://cloudsecurityalliance.org/research/cloud-controls-matrix)
* MITRE ATT&CK: [T1070](https://attack.mitre.org/techniques/T1070/), [T1565.001](https://attack.mitre.org/techniques/T1565/001/)
* NYDFS: [23 NYCRR 500.6](https://www.law.cornell.edu/regulations/new-york/23-NYCRR-500.6)
* SWIFT CSCF: [control 6.4](https://compliance.theartofservice.com/controls/swift-cscf/cscf-6-4)
* ICH E6(R3): [ICH efficacy guidelines](https://www.ich.org/page/efficacy-guidelines),
  [Annex 1, section 4 summary](https://open-exam-prep.com/study-guides/acrp-cp/chapter-12-data-management-edc/computerised-systems-data-governance)

Last reviewed: November 2026. Regulations and their application dates change; check the sources before relying on
a date.
