"""Signature verification."""

from __future__ import annotations

import enum
import hashlib
import hmac
from typing import Mapping, Protocol

MINIMUM_KEY_BYTES = 32


class SignatureCheck(enum.Enum):
    VALID = "valid"
    INVALID = "invalid"
    UNKNOWN_KEY = "unknown_key"


class SignatureVerifier(Protocol):
    """Checks signatures. Implement it to verify with keys held elsewhere (KMS, HSM)."""

    def verify(self, key_id: str, digest: bytes, signature: bytes) -> SignatureCheck: ...


class LedgerSigner(SignatureVerifier, Protocol):
    """Signs entry and checkpoint digests. Implement it to keep the key in a KMS or HSM. Must be thread safe.

    The writer calls :meth:`sign` once per entry: a remote signer adds its latency to every append.
    """

    @property
    def key_id(self) -> str: ...

    def sign(self, digest: bytes) -> bytes: ...


class HmacVerifier:
    """HMAC-SHA256 verification with one or several keys (current and retired), by key id.

    HMAC is symmetric: whoever can verify could also sign. Give auditors the key through a channel the
    application operators do not control, or rely on checkpoints stored in trusted storage.
    """

    def __init__(self, keys: Mapping[str, str | bytes]):
        if not keys:
            raise ValueError("At least one key is required.")
        self._keys: dict[str, bytes] = {}
        for key_id, secret in keys.items():
            raw = secret.encode("utf-8") if isinstance(secret, str) else bytes(secret)
            if not key_id:
                raise ValueError("Key ids cannot be empty.")
            if len(raw) < MINIMUM_KEY_BYTES:
                raise ValueError(f"Key '{key_id}' is {len(raw)} bytes long, the minimum is {MINIMUM_KEY_BYTES}.")
            self._keys[key_id] = raw

    @property
    def key_ids(self) -> list[str]:
        return sorted(self._keys)

    def sign(self, key_id: str, digest: bytes) -> bytes:
        """Signs a digest. Used by tests and tools; the verifier itself never signs."""
        return hmac.new(self._keys[key_id], digest, hashlib.sha256).digest()

    def verify(self, key_id: str, digest: bytes, signature: bytes) -> SignatureCheck:
        key = self._keys.get(key_id)
        if key is None:
            return SignatureCheck.UNKNOWN_KEY
        expected = hmac.new(key, digest, hashlib.sha256).digest()
        return SignatureCheck.VALID if hmac.compare_digest(expected, signature) else SignatureCheck.INVALID


_PLACEHOLDERS = {"DEFAULT_INSECURE_KEY_CHANGE_ME", "CHANGE_ME", "CHANGE_ME_IN_PROD", "MY_SUPER_SECRET_LOCAL_DEV_KEY_12345"}


class HmacLedgerSigner:
    """HMAC-SHA256 signer with a current key and optional retired keys (used only to verify old entries).

    HMAC is symmetric: whoever can verify can also sign. Combine it with external checkpoints, or use a signer
    backed by a KMS, if the people who run the application must not be able to rewrite history.
    """

    def __init__(self, key_id: str, secret_key: str | None, retired_keys: Mapping[str, str] | None = None):
        if not key_id or not key_id.strip():
            raise ValueError("PROVANCE: signing_key_id is required.")
        keys: dict[str, str | bytes] = {key_id: _checked(key_id, secret_key)}
        for retired_id, secret in (retired_keys or {}).items():
            if retired_id == key_id:
                raise ValueError(f"PROVANCE: key id '{retired_id}' is both the signing key and a retired key.")
            keys[retired_id] = _checked(retired_id, secret)
        self._key_id = key_id
        self._verifier = HmacVerifier(keys)

    @property
    def key_id(self) -> str:
        return self._key_id

    def sign(self, digest: bytes) -> bytes:
        return self._verifier.sign(self._key_id, digest)

    def verify(self, key_id: str, digest: bytes, signature: bytes) -> SignatureCheck:
        return self._verifier.verify(key_id, digest, signature)


def _checked(key_id: str, secret: str | None) -> str:
    if not secret or not secret.strip():
        raise ValueError(f"PROVANCE: no secret configured for key '{key_id}'. Load it from a secret store or an "
                         "environment variable, or pass your own signer.")
    if secret.upper() in _PLACEHOLDERS:
        raise ValueError(f"PROVANCE: the secret of key '{key_id}' is a placeholder value. Generate a real key.")
    length = len(secret.encode("utf-8"))
    if length < MINIMUM_KEY_BYTES:
        raise ValueError(f"PROVANCE: the secret of key '{key_id}' is {length} bytes long, the minimum is {MINIMUM_KEY_BYTES}.")
    return secret

