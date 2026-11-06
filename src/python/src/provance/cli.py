"""Command line: ``provance verify`` and ``provance export``.

Exit codes: 0 the ledger is valid, 1 verification failed, 2 usage or connection error.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Sequence

from . import __version__
from .models import LedgerCheckpoint
from .signing import HmacVerifier
from .stores import DEFAULT_TABLE, JsonLinesReader, SqlLedgerReader, connect, write_json_lines
from .verifier import LedgerVerifier

DEFAULT_KEY_ENV = "PROVANCE_SECRET_KEY"


class UsageError(Exception):
    pass


def main(argv: Sequence[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if not getattr(args, "command", None):
        parser.print_help()
        return 2
    try:
        return args.handler(args)
    except (UsageError, ValueError, FileNotFoundError, RuntimeError) as e:
        print(f"provance: {e}", file=sys.stderr)
        return 2
    except Exception as e:  # driver errors (connection refused, unknown table, ...)
        print(f"provance: {type(e).__name__}: {e}", file=sys.stderr)
        return 2


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="provance", description="Verify and export PROVANCE tamper-evident ledgers.")
    p.add_argument("--version", action="version", version=f"provance {__version__}")
    sub = p.add_subparsers(dest="command")

    v = sub.add_parser("verify", help="verify a ledger from a database or a JSON Lines export",
                       description="Verify the hash chain, signatures and, with --checkpoint, truncation and rewrites.")
    _add_source(v)
    v.add_argument("--ledger-id", required=True, help="ledger id configured in the application")
    v.add_argument("--genesis-hash", help="only if the application configured an explicit genesis hash")
    v.add_argument("--key-id", default="k1", help="id of the current HMAC key (default: k1)")
    v.add_argument("--key-env", default=DEFAULT_KEY_ENV, metavar="VAR",
                   help=f"environment variable holding the HMAC secret (default: {DEFAULT_KEY_ENV})")
    v.add_argument("--key-file", metavar="PATH", help="file holding the HMAC secret, instead of --key-env")
    v.add_argument("--retired-key", action="append", default=[], metavar="ID=VAR",
                   help="retired key: key id and the environment variable holding its secret (repeatable)")
    v.add_argument("--checkpoint", metavar="PATH",
                   help="trusted checkpoint: a checkpoint JSON file, or a checkpoint directory (latest one is used)")
    v.add_argument("--from-checkpoint", action="store_true", help="verify only from the checkpoint onward")
    v.add_argument("--json", action="store_true", help="print the result as JSON")
    v.set_defaults(handler=_verify)

    e = sub.add_parser("export", help="export a ledger table to JSON Lines (for an offline audit)")
    _add_db(e, required=True)
    e.add_argument("--from", dest="from_sequence", type=int, default=1, metavar="SEQ", help="first sequence (default: 1)")
    e.add_argument("-o", "--output", metavar="PATH", help="output file (default: standard output)")
    e.set_defaults(handler=_export)
    return p


def _add_db(p: argparse.ArgumentParser, required: bool) -> None:
    p.add_argument("--db", required=required, metavar="URL",
                   help="database URL: sqlite:///audit.db, postgresql://user@host/db, mysql://..., mssql://...")
    p.add_argument("--table", default=DEFAULT_TABLE, help=f"ledger table (default: {DEFAULT_TABLE})")
    p.add_argument("--schema", help="schema of the ledger table")


def _add_source(p: argparse.ArgumentParser) -> None:
    _add_db(p, required=False)
    p.add_argument("--file", metavar="PATH", help="JSON Lines export produced by 'provance export'")


def _open_source(args: argparse.Namespace):
    file = getattr(args, "file", None)
    if bool(args.db) == bool(file):
        raise UsageError("give exactly one of --db or --file")
    if file:
        return JsonLinesReader(file), None
    connection, dialect = connect(args.db)
    return SqlLedgerReader(connection, dialect, table=args.table, schema=args.schema), connection


def _secret_from_env(var: str, what: str) -> str:
    value = os.environ.get(var)
    if not value:
        raise UsageError(f"environment variable {var} is not set ({what})")
    return value


def _keys(args: argparse.Namespace) -> dict[str, str]:
    if args.key_file:
        secret = Path(args.key_file).read_text(encoding="utf-8").rstrip("\r\n")
    else:
        secret = _secret_from_env(args.key_env, f"secret of key '{args.key_id}'")
    keys = {args.key_id: secret}
    for spec in args.retired_key:
        key_id, sep, var = spec.partition("=")
        if not sep or not key_id or not var:
            raise UsageError(f"--retired-key expects ID=ENV_VAR, got '{spec}'")
        keys[key_id] = _secret_from_env(var, f"secret of retired key '{key_id}'")
    return keys


def _load_checkpoint(path_text: str | None, ledger_id: str) -> LedgerCheckpoint | None:
    if not path_text:
        return None
    path = Path(path_text)
    if path.is_dir():
        folder = path / ledger_id if (path / ledger_id).is_dir() else path
        files = [f for f in folder.glob("*.json") if f.stem.isdigit()]
        if not files:
            raise UsageError(f"no checkpoint file (NNNN.json) in {folder}")
        path = max(files, key=lambda f: int(f.stem))
    return LedgerCheckpoint.from_json(path.read_text(encoding="utf-8"))


def _verify(args: argparse.Namespace) -> int:
    if args.from_checkpoint and not args.checkpoint:
        raise UsageError("--from-checkpoint needs --checkpoint")
    keys = _keys(args)
    checkpoint = _load_checkpoint(args.checkpoint, args.ledger_id)
    source, connection = _open_source(args)
    try:
        verifier = LedgerVerifier(source, HmacVerifier(keys), args.ledger_id, args.genesis_hash)
        result = verifier.verify(checkpoint=checkpoint, start_from_checkpoint=args.from_checkpoint)
    finally:
        if connection is not None:
            connection.close()

    if args.json:
        print(json.dumps(result.to_dict(), indent=2))
    else:
        status = "OK" if result.valid else f"FAILED ({result.failure.value})"
        print(f"{status}: {result.message}")
        if checkpoint is not None and result.valid:
            print(f"Checkpoint at sequence {checkpoint.sequence} matched.")
        if result.redacted_payloads:
            print(f"{result.redacted_payloads} payload(s) redacted.")
        if result.valid and result.last_hash:
            print(f"Head: sequence {result.last_sequence}, hash {result.last_hash}")
    return 0 if result.valid else 1


def _export(args: argparse.Namespace) -> int:
    source, connection = _open_source(args)
    try:
        if args.output:
            with open(args.output, "w", encoding="utf-8", newline="\n") as out:
                count = write_json_lines(source.read(args.from_sequence), out)
            print(f"{count} entries written to {args.output}", file=sys.stderr)
        else:
            write_json_lines(source.read(args.from_sequence), sys.stdout)
    finally:
        connection.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
