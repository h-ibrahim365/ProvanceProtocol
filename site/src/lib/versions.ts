// Library versions shown in the version menu. Add a release here, with its steps, when it ships.

import { type CodeSample, type Step, STEPS_V1, STEPS_V2 } from "./demo/steps";

export interface Version {
  id: string;
  label: string;
  /** ISO date, or null when planned. */
  released: string | null;
  /** Can be tried in the demo before its release. */
  preview?: boolean;
  /** Erasures write a signed redaction record, and verification requires it (0.2). */
  recordedRedactions: boolean;
  formatVersion: number;
  summary: string;
  changes: string[];
  steps: Step[];
  code: Partial<Record<CodeSample, Record<Language, string>>>;
}

export type Language = "dotnet" | "java" | "python";

export const LANGUAGES: { id: Language; label: string; install: string }[] = [
  { id: "dotnet", label: ".NET", install: "dotnet add package Provance.Core" },
  { id: "java", label: "Java", install: "io.github.h-ibrahim365:provance-core" },
  { id: "python", label: "Python", install: "pip install provance" },
];

const CODE_V1: Version["code"] = {
  setup: {
    dotnet: `builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))   // LedgerId, SigningKeyId, SecretKey
    .UseEntityFrameworkCore(db => db.UseNpgsql(connectionString));`,
    java: `ProvanceLedger ledger = ProvanceLedger.builder()
    .ledgerId("orders-api-prod")
    .secretKey(System.getenv("PROVANCE_SECRET_KEY"))
    .store(new JdbcLedgerStore(dataSource))
    .build();`,
    python: `ledger = ProvanceLedger(
    ledger_id="orders-api-prod",
    secret_key=os.environ["PROVANCE_SECRET_KEY"],
    store=SqlLedgerStore.from_url("postgresql://app@db/audit"),
)`,
  },
  record: {
    dotnet: `await ledger.AddEntryAsync("ORDER_REFUNDED", new AuditedPayload
{
    ActorId = "support-7",
    Data = new Dictionary<string, object?> { ["orderId"] = "A-1001", ["amount"] = "49.90" }
});`,
    java: `ledger.addEntry("ORDER_REFUNDED", AuditedPayload.of("support-7", "Refund issued",
    Map.of("orderId", "A-1001", "amount", "49.90")));`,
    python: `ledger.add_entry("ORDER_REFUNDED", AuditedPayload(
    actor_id="support-7",
    data={"orderId": "A-1001", "amount": "49.90"},
))`,
  },
  verify: {
    dotnet: `var result = await ledger.VerifyAsync();
// result.IsValid, result.Failure, result.FailedAtSequence`,
    java: `LedgerVerificationResult result = ledger.verify();
// result.valid(), result.failure(), result.failedAtSequence()`,
    python: `result = ledger.verify()
# result.valid, result.failure, result.failed_at_sequence`,
  },
  checkpoint: {
    dotnet: `var checkpoint = await ledger.CreateCheckpointAsync();   // store it outside the database
var result = await ledger.VerifyAsync(
    new LedgerVerificationOptions { TrustedCheckpoint = checkpoint });`,
    java: `LedgerCheckpoint checkpoint = ledger.createCheckpoint().orElseThrow();   // store it outside the database
LedgerVerificationResult result = ledger.verify(LedgerVerificationOptions.withCheckpoint(checkpoint));`,
    python: `checkpoint = ledger.create_checkpoint()   # store it outside the database
result = ledger.verify(checkpoint)`,
  },
  redact: {
    dotnet: `await ledger.RedactPayloadAsync(entryId);`,
    java: `ledger.redactPayload(entryId);`,
    python: `ledger.redact_payload(entry_id)`,
  },
};

const CODE_V2: Version["code"] = {
  ...CODE_V1,
  setup: {
    dotnet: `builder.Services
    .AddProvance(builder.Configuration.GetSection("Provance"))   // LedgerId, keys, Retention:KeepFor
    .UseEntityFrameworkCore(db => db.UseNpgsql(appConnectionString))
    .UseEntityFrameworkCoreArchive(db => db.UseNpgsql(archiveConnectionString));`,
    java: `ProvanceLedger ledger = ProvanceLedger.builder()
    .ledgerId("orders-api-prod")
    .secretKey(System.getenv("PROVANCE_SECRET_KEY"))
    .store(new JdbcLedgerStore(dataSource))
    .archive(new JdbcLedgerStore(archiveDataSource, archiveTable))   // table provance_archive
    .build();`,
    python: `ledger = ProvanceLedger(
    ledger_id="orders-api-prod",
    secret_key=os.environ["PROVANCE_SECRET_KEY"],
    store=SqlLedgerStore.from_url("postgresql://app@db/audit"),
    archive=SqlLedgerStore.from_url("postgresql://app@archive/audit", table="provance_archive"),
)`,
  },
  redact: {
    dotnet: `await ledger.RedactPayloadAsync(entryId);   // appends a signed provance.redaction record first`,
    java: `ledger.redactPayload(entryId);   // appends a signed provance.redaction record first`,
    python: `ledger.redact_payload(entry_id)   # appends a signed provance.redaction record first`,
  },
  archive: {
    dotnet: `LedgerComparison comparison = await ledger.CompareWithArchiveAsync();
if (!comparison.IsIdentical)
    logger.LogCritical("{Report}", comparison.Describe());   // field, archive value -> current value`,
    java: `LedgerComparison comparison = ledger.compareWithArchive();
if (!comparison.identical())
    log.error(comparison.describe());   // field, archive value -> current value`,
    python: `comparison = ledger.compare_with_archive()
if not comparison.identical:
    log.critical(comparison.describe())   # field, archive value -> current value

# or from the command line:
# provance diff postgresql://auditor@db/audit postgresql://auditor@archive/audit --ledger-id orders-api-prod`,
  },
  retention: {
    dotnet: `// or in appsettings.json: "Retention": { "KeepFor": "7.00:00:00" }
LedgerRetentionResult result = await ledger.ApplyRetentionAsync(TimeSpan.FromDays(7));`,
    java: `// Spring Boot: provance.retention-keep-for=7d
RetentionResult result = ledger.applyRetention(Duration.ofDays(7));`,
    python: `ledger = ProvanceLedger(..., archive=archive, retention_keep_for=timedelta(days=7))
result = ledger.apply_retention(timedelta(days=7))`,
  },
};

export const VERSIONS: Version[] = [
  {
    id: "0.1.0",
    label: "0.1.0",
    released: "2026-11-06",
    formatVersion: 1,
    recordedRedactions: false,
    summary: "First release of ledger format 1, in .NET, Java and Python.",
    changes: [
      "Hash chained entries with SHA-256, signed with HMAC-SHA256",
      "Key rotation with retired keys",
      "Signed checkpoints, published automatically to the storage you choose",
      "Payload redaction for erasure requests",
      "Several application instances writing to one ledger",
      "One entry per HTTP request (ASP.NET Core, Spring, FastAPI, Flask, Django)",
    ],
    steps: STEPS_V1,
    code: CODE_V1,
  },
  {
    id: "0.2.0",
    label: "0.2.0",
    released: "2026-11-07",
    formatVersion: 1,
    recordedRedactions: true,
    summary: "Archive copy, field-level comparison, retention and recorded erasures, in .NET, Java and Python. Same ledger format.",
    changes: [
      "Several ledgers in one application",
      "Erasures leave a signed record: a payload set to NULL in the database is detected",
      "A copy of every entry in a second database, such as an archive",
      "A comparison that shows which field was changed and its original value",
      "Retention: old entries leave the main database once archived, and the ledger still verifies",
      "Ledgers written by each language verified by the other two in every test suite",
    ],
    steps: STEPS_V2,
    code: CODE_V2,
  },
];

/** The latest released version: what the demo shows first. */
export const CURRENT = VERSIONS.filter((v) => v.released).at(-1)!;

/** Versions the demo can show: released, or open as a preview. */
export function tryable(v: Version): boolean {
  return v.released !== null || v.preview === true;
}
