"""Checkpoint sinks and automatic publication.

A checkpoint is only as trustworthy as the place it is stored: use storage the application cannot overwrite
(object storage with a retention lock, WORM storage, a timestamping service, a system owned by another team).
"""

from __future__ import annotations

import logging
import os
import threading
import uuid
from datetime import timedelta
from pathlib import Path
from typing import TYPE_CHECKING, Callable, Protocol, Sequence

from .models import LedgerCheckpoint

if TYPE_CHECKING:
    from .ledger import ProvanceLedger

log = logging.getLogger("provance.checkpoints")


class LedgerCheckpointSink(Protocol):
    """Destination of automatic checkpoints. Must not overwrite an existing checkpoint."""

    def publish(self, checkpoint: LedgerCheckpoint) -> None: ...


class FileCheckpointSink:
    """Writes each checkpoint to ``{directory}/{ledger_id}/{sequence:016}.json`` and never overwrites a file.

    Same layout as the .NET and Java file sinks. Useful when the directory is mounted from immutable storage.
    """

    def __init__(self, directory: str | Path):
        self.directory = Path(directory)

    def publish(self, checkpoint: LedgerCheckpoint) -> None:
        folder = _folder(self.directory, checkpoint.ledger_id)
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / f"{checkpoint.sequence:016d}.json"
        if _is_valid(path, checkpoint):
            return  # same head already published, possibly by another instance

        # Written to a temporary file, then linked into place: a crash never leaves a truncated checkpoint behind.
        temp = folder / f".{path.name}.{uuid.uuid4().hex}.tmp"
        try:
            with open(temp, "x", encoding="utf-8", newline="\n") as f:
                f.write(checkpoint.to_json())
                f.flush()
                os.fsync(f.fileno())
            try:
                os.link(temp, path)  # fails if the file exists, unlike os.replace
            except FileExistsError:
                # Published concurrently, or a broken file left by a crash of an older version: only the latter is replaced.
                if not _is_valid(path, checkpoint):
                    os.replace(temp, path)
            except OSError:
                # File systems without hard links: exclusive creation, then an atomic replace of our own file.
                if not path.exists() or not _is_valid(path, checkpoint):
                    os.replace(temp, path)
        finally:
            try:
                temp.unlink()
            except FileNotFoundError:
                pass


def read_latest_checkpoint(directory: str | Path, ledger_id: str) -> LedgerCheckpoint | None:
    """Reads the checkpoint with the highest sequence written by a file sink, or ``None``. A file that does not parse
    (left by a crash) is skipped rather than hiding the older valid ones."""
    folder = _folder(Path(directory), ledger_id)
    files = [f for f in folder.glob("*.json") if f.stem.isdigit()] if folder.is_dir() else []
    for file in sorted(files, key=lambda f: int(f.stem), reverse=True):
        checkpoint = _try_read(file)
        if checkpoint is not None:
            return checkpoint
    return None


def _try_read(path: Path) -> LedgerCheckpoint | None:
    try:
        return LedgerCheckpoint.from_json(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, KeyError, TypeError):
        return None


def _is_valid(path: Path, expected: LedgerCheckpoint) -> bool:
    existing = _try_read(path)
    return existing is not None and existing.sequence == expected.sequence and existing.ledger_id == expected.ledger_id


def _folder(directory: Path, ledger_id: str) -> Path:
    if not ledger_id or ledger_id in (".", "..") or "/" in ledger_id or "\\" in ledger_id:
        raise ValueError(f"Ledger id '{ledger_id}' cannot be used as a folder name.")
    return directory / ledger_id


class CheckpointPublisher:
    """Publishes a checkpoint of the head to every sink at the interval, when the head has moved."""

    def __init__(self, create: Callable[[], LedgerCheckpoint | None], sinks: Sequence[LedgerCheckpointSink],
                 interval: timedelta, name: str):
        self._create = create
        self._sinks = list(sinks)
        self._interval = interval.total_seconds()
        self._stop = threading.Event()
        self._last_sequence = 0
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, name=f"provance-checkpoints-{name}", daemon=True)

    def start(self) -> None:
        if self._sinks:
            self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread.is_alive():
            self._thread.join(timeout=5)

    def publish_once(self) -> None:
        with self._lock:
            try:
                checkpoint = self._create()
                if checkpoint is None or checkpoint.sequence == self._last_sequence:
                    return
                for sink in self._sinks:
                    sink.publish(checkpoint)
                self._last_sequence = checkpoint.sequence
                log.info("PROVANCE checkpoint published at sequence %d.", checkpoint.sequence)
            except Exception:
                log.exception("PROVANCE checkpoint publication failed; it will be retried at the next interval.")

    def _run(self) -> None:
        while not self._stop.wait(self._interval):
            self.publish_once()
