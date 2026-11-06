"""PROVANCE for Python: tamper-evident audit ledgers.

Write audit events into a hash chained, signed ledger, and verify ledgers written by any PROVANCE implementation
(.NET, Java, Python). See docs/SPEC.md for the format.

    from provance import AuditedPayload, ProvanceLedger, SqlLedgerStore

    ledger = ProvanceLedger(ledger_id="orders-api-prod", secret_key=os.environ["PROVANCE_SECRET_KEY"],
                            store=SqlLedgerStore.from_url("postgresql://app@db/audit"))
    ledger.add_entry("ORDER_REFUNDED", AuditedPayload(actor_id="user-42", description="Refund issued"))
    result = ledger.verify()
"""

from importlib.metadata import PackageNotFoundError, version

from .checkpoints import FileCheckpointSink, LedgerCheckpointSink, read_latest_checkpoint
from .errors import (
    LedgerConcurrencyError,
    LedgerError,
    LedgerOverloadedError,
    LedgerStoreError,
    LedgerUnavailableError,
    PayloadError,
)
from .format import (
    FORMAT_VERSION,
    canonical_checkpoint,
    canonical_envelope,
    compute_checkpoint_digest,
    compute_entry_hash,
    derive_genesis_hash,
    format_timestamp,
    parse_timestamp,
    sha256_hex,
)
from .ledger import ProvanceLedger
from .models import AuditedPayload, LedgerCheckpoint, LedgerEntry
from .options import ProvanceOptions, QueueFullBehavior
from .payload import canonical_payload
from .signing import HmacLedgerSigner, HmacVerifier, LedgerSigner, SignatureCheck, SignatureVerifier
from .stores import (
    InMemoryLedgerStore,
    JsonLinesReader,
    LedgerStore,
    ListSource,
    SqlLedgerReader,
    SqlLedgerStore,
    connect,
    connection_factory,
    write_json_lines,
)
from .verifier import LedgerTamperedError, LedgerVerifier, VerificationFailure, VerificationResult

try:
    __version__ = version("provance")
except PackageNotFoundError:  # running from a source checkout
    __version__ = "0.0.0"

__all__ = [
    "FORMAT_VERSION",
    "AuditedPayload",
    "FileCheckpointSink",
    "HmacLedgerSigner",
    "HmacVerifier",
    "InMemoryLedgerStore",
    "JsonLinesReader",
    "LedgerCheckpoint",
    "LedgerCheckpointSink",
    "LedgerConcurrencyError",
    "LedgerEntry",
    "LedgerError",
    "LedgerOverloadedError",
    "LedgerSigner",
    "LedgerStore",
    "LedgerStoreError",
    "LedgerTamperedError",
    "LedgerUnavailableError",
    "LedgerVerifier",
    "ListSource",
    "PayloadError",
    "ProvanceLedger",
    "ProvanceOptions",
    "QueueFullBehavior",
    "SignatureCheck",
    "SignatureVerifier",
    "SqlLedgerReader",
    "SqlLedgerStore",
    "VerificationFailure",
    "VerificationResult",
    "canonical_checkpoint",
    "canonical_envelope",
    "canonical_payload",
    "compute_checkpoint_digest",
    "compute_entry_hash",
    "connect",
    "connection_factory",
    "derive_genesis_hash",
    "format_timestamp",
    "parse_timestamp",
    "read_latest_checkpoint",
    "sha256_hex",
    "write_json_lines",
]
