"""The provance command line."""

from __future__ import annotations

import json

from provance import LedgerCheckpoint, format_timestamp, parse_timestamp
from provance.cli import main

from conftest import LEDGER_ID, SECRET, VECTORS, seal
from test_sql import sqlite_db
from test_verifier import checkpoint_of

JAVA = VECTORS / "ledger-java"


def test_verify_java_export_with_rotated_keys_and_checkpoint(monkeypatch, capsys):
    monkeypatch.setenv("K2", "provance-test-vector-key-2-abcdef0123456789")
    monkeypatch.setenv("K1", "provance-test-vector-key-0123456789abcdef")
    code = main(["verify", "--file", str(JAVA / "entries.jsonl"), "--ledger-id", "cross-language-test",
                 "--key-id", "k2", "--key-env", "K2", "--retired-key", "k1=K1",
                 "--checkpoint", str(JAVA / "checkpoint.json"), "--accept-unrecorded-redactions"])
    out = capsys.readouterr().out
    assert code == 0, out
    assert out.startswith("OK: 6 entries verified")
    assert "Checkpoint at sequence 6 matched." in out
    assert "1 payload(s) redacted." in out


def test_java_0_1_export_has_an_unrecorded_erasure(monkeypatch, capsys):
    monkeypatch.setenv("K2", "provance-test-vector-key-2-abcdef0123456789")
    monkeypatch.setenv("K1", "provance-test-vector-key-0123456789abcdef")
    code = main(["verify", "--file", str(JAVA / "entries.jsonl"), "--ledger-id", "cross-language-test",
                 "--key-id", "k2", "--key-env", "K2", "--retired-key", "k1=K1"])
    assert code == 1
    assert "FAILED (unrecorded_redaction)" in capsys.readouterr().out


def test_diff_shows_the_original_value_of_a_changed_field(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("K2", "provance-test-vector-key-2-abcdef0123456789")
    monkeypatch.setenv("K1", "provance-test-vector-key-0123456789abcdef")
    archive = JAVA / "entries.jsonl"
    lines = archive.read_text(encoding="utf-8").split("\n")
    entry = json.loads(lines[1])
    entry["payloadJson"] = entry["payloadJson"].replace("49.90", "4990.00")
    lines[1] = json.dumps(entry)
    primary = tmp_path / "primary.jsonl"
    primary.write_text("\n".join(lines), encoding="utf-8")

    code = main(["diff", str(primary), str(archive), "--ledger-id", "cross-language-test",
                 "--key-id", "k2", "--key-env", "K2", "--retired-key", "k1=K1"])
    out = capsys.readouterr().out
    assert code == 1
    assert "Entry 2 (ORDER_REFUNDED) was changed in the primary store" in out
    assert 'payload.data.amount: "49.90" -> "4990.00"' in out

    assert main(["diff", str(archive), str(archive), "--ledger-id", "cross-language-test", "--key-id", "k2",
                 "--key-env", "K2", "--retired-key", "k1=K1"]) == 0


def test_failed_verification_exits_with_1_and_json(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("PROVANCE_SECRET_KEY", SECRET)
    db = sqlite_db(tmp_path / "audit.db", seal(3))
    db.execute("UPDATE provance_ledger SET event_type = 'X' WHERE seq = 3")
    db.commit()
    db.close()

    code = main(["verify", "--db", f"sqlite:///{tmp_path / 'audit.db'}", "--ledger-id", LEDGER_ID, "--json"])
    result = json.loads(capsys.readouterr().out)
    assert code == 1
    assert result["valid"] is False
    assert result["failure"] == "hash_mismatch"
    assert result["failed_at_sequence"] == 3


def test_export_then_verify_offline_with_a_checkpoint_directory(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("PROVANCE_SECRET_KEY", SECRET)
    entries = seal(5)
    sqlite_db(tmp_path / "audit.db", entries).close()
    # Same layout as the file checkpoint sinks: {dir}/{ledgerId}/{sequence:016}.json
    folder = tmp_path / "checkpoints" / LEDGER_ID
    folder.mkdir(parents=True)
    (folder / "0000000000000003.json").write_text(checkpoint_of(entries, 3).to_json(), encoding="utf-8")
    (folder / "0000000000000005.json").write_text(checkpoint_of(entries).to_json(), encoding="utf-8")

    assert main(["export", "--db", f"sqlite:///{tmp_path / 'audit.db'}", "-o", str(tmp_path / "ledger.jsonl")]) == 0
    assert len((tmp_path / "ledger.jsonl").read_text(encoding="utf-8").splitlines()) == 5

    code = main(["verify", "--file", str(tmp_path / "ledger.jsonl"), "--ledger-id", LEDGER_ID,
                 "--checkpoint", str(tmp_path / "checkpoints")])
    out = capsys.readouterr().out
    assert code == 0, out
    assert "Checkpoint at sequence 5 matched." in out


def test_usage_errors_exit_with_2(tmp_path, monkeypatch, capsys):
    monkeypatch.delenv("PROVANCE_SECRET_KEY", raising=False)
    assert main(["verify", "--file", str(JAVA / "entries.jsonl"), "--ledger-id", "x"]) == 2  # no key
    monkeypatch.setenv("PROVANCE_SECRET_KEY", SECRET)
    assert main(["verify", "--ledger-id", "x"]) == 2  # no source
    assert main(["verify", "--db", "sqlite:///does-not-exist.db", "--ledger-id", "x"]) == 2
    assert main(["verify", "--db", "oracle://x", "--ledger-id", "x"]) == 2
    assert "provance:" in capsys.readouterr().err


def test_key_file(tmp_path, capsys):
    (tmp_path / "key").write_text(SECRET + "\n", encoding="utf-8")
    sqlite_db(tmp_path / "audit.db", seal(2)).close()
    assert main(["verify", "--db", f"sqlite:///{tmp_path / 'audit.db'}", "--ledger-id", LEDGER_ID,
                 "--key-file", str(tmp_path / "key")]) == 0


def test_checkpoint_json_from_dotnet_is_readable():
    # System.Text.Json writes the offset and omits a zero fraction.
    text = ('{"v":1,"ledgerId":"orders-api-test","sequence":3,"hash":"' + "a" * 64 + '",'
            '"createdAt":"2026-01-15T10:00:00+00:00","keyId":"k1","signature":"' + "b" * 64 + '"}')
    checkpoint = LedgerCheckpoint.from_json(text)
    assert format_timestamp(checkpoint.created_at) == "2026-01-15T10:00:00.000Z"
    assert LedgerCheckpoint.from_json(checkpoint.to_json()) == checkpoint


def test_timestamp_forms_written_by_the_stores():
    expected = "2026-01-15T09:30:00.123Z"
    for text in ["2026-01-15T09:30:00.123Z", "2026-01-15 09:30:00.123+00:00", "2026-01-15 09:30:00.1230000 +00:00",
                 "2026-01-15T10:30:00.123456+01:00", "2026-01-15 09:30:00.123"]:
        assert format_timestamp(parse_timestamp(text)) == expected, text
    assert format_timestamp(parse_timestamp("2026-01-15T09:30:00Z")) == "2026-01-15T09:30:00.000Z"
