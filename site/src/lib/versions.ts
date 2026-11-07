// Library versions shown in the version menu. Add a release here, with its steps, when it ships.

import { type CodeSample, type Step, STEPS_V1 } from "./demo/steps";

export interface Version {
  id: string;
  label: string;
  /** ISO date, or null when planned. */
  released: string | null;
  formatVersion: number;
  summary: string;
  changes: string[];
  steps: Step[];
  code: Record<CodeSample, Record<Language, string>>;
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

export const VERSIONS: Version[] = [
  {
    id: "0.1.0",
    label: "0.1.0",
    released: "2026-11-06",
    formatVersion: 1,
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
    label: "0.2.0 (planned)",
    released: null,
    formatVersion: 1,
    summary: "Planned. Shown here once it is released.",
    changes: [
      "Public key signatures (ECDSA), so auditors verify without being able to sign",
      "Several ledgers in one application",
      "A copy of every entry in a second database, such as an archive",
      "A comparison that shows which field was changed and its original value",
      "Checkpoints sent to immutable storage and timestamping services",
    ],
    steps: STEPS_V1,
    code: CODE_V1,
  },
];

export const CURRENT = VERSIONS[0]!;
