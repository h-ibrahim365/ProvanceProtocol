"""Turning payload objects into canonical JSON (RFC 8785).

Supported without configuration: ``dict`` and other mappings, lists, tuples and sets, strings, numbers, booleans,
``None``, dataclasses, Pydantic models, enums, ``datetime``/``date``/``time``, ``UUID``, ``Decimal``, ``bytes``
(Base64), objects with public attributes, and any object with a ``__provance_payload__()`` method.

Attributes that are ``None`` are omitted from dataclasses and objects (as in .NET and Java); ``None`` values in
dictionaries are kept. Numbers become IEEE 754 doubles once canonicalized: write money amounts and large integers
as strings.
"""

from __future__ import annotations

import base64
import dataclasses
import enum
import math
import uuid
from collections.abc import Mapping
from datetime import date, datetime, time
from decimal import Decimal
from typing import Any, Callable

import rfc8785

from .errors import PayloadError
from .format import MAX_SEQUENCE

MAX_DEPTH = 64

PayloadSerializer = Callable[[Any], Any]
"""Converts a payload object into JSON compatible values (dict, list, str, int, float, bool, None)."""


def to_json_value(value: Any, depth: int = 0) -> Any:
    """Converts ``value`` into plain JSON values, following the rules of this module."""
    if depth > MAX_DEPTH:
        raise PayloadError(f"Payload is nested more than {MAX_DEPTH} levels deep (or contains a cycle).")
    if value is None or isinstance(value, (str, bool)):
        return value
    if isinstance(value, int) and not isinstance(value, enum.Enum):
        # The .NET and Java libraries canonicalize every number as a double; do the same for large integers.
        return value if abs(value) <= MAX_SEQUENCE else float(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise PayloadError("NaN and Infinity cannot be written to JSON.")
        return value
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise PayloadError("NaN and Infinity cannot be written to JSON.")
        return float(value)
    if hasattr(value, "__provance_payload__"):
        return to_json_value(value.__provance_payload__(), depth + 1)
    if isinstance(value, enum.Enum):
        inner = value.value
        return inner if isinstance(inner, (str, int, float, bool)) else value.name
    if isinstance(value, (datetime, date, time)):
        return value.isoformat()
    if isinstance(value, uuid.UUID):
        return str(value)
    if isinstance(value, (bytes, bytearray, memoryview)):
        return base64.b64encode(bytes(value)).decode("ascii")
    if isinstance(value, Mapping):
        result = {}
        for k, v in value.items():
            if isinstance(k, enum.Enum):
                k = k.name
            elif isinstance(k, (int, float, uuid.UUID)) and not isinstance(k, bool):
                k = str(k)
            if not isinstance(k, str):
                raise PayloadError(f"Mapping keys must be strings, got {type(k).__name__}.")
            result[k] = to_json_value(v, depth + 1)
        return result
    if isinstance(value, (list, tuple, set, frozenset)):
        return [to_json_value(v, depth + 1) for v in value]
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {f.name: to_json_value(getattr(value, f.name), depth + 1)
                for f in dataclasses.fields(value) if getattr(value, f.name) is not None}
    if hasattr(value, "model_dump") and callable(value.model_dump):  # Pydantic v2
        return to_json_value(value.model_dump(mode="json", exclude_none=True), depth + 1)
    if hasattr(value, "__dict__") and not isinstance(value, type):
        public = {k: v for k, v in vars(value).items() if not k.startswith("_") and v is not None and not callable(v)}
        if public:
            return {k: to_json_value(v, depth + 1) for k, v in public.items()}
    raise PayloadError(f"Values of type {type(value).__name__} cannot be serialized. Use a dict, a dataclass, "
                       "a Pydantic model, or pass a payload_serializer.")


def canonical_payload(payload: Any, serializer: PayloadSerializer | None = None) -> str:
    """Canonical JSON text of a payload.

    :raises PayloadError: the payload cannot be serialized
    """
    if payload is None:
        raise PayloadError("Payload is required.")
    try:
        value = serializer(payload) if serializer else payload
        return rfc8785.dumps(to_json_value(value)).decode("utf-8")
    except PayloadError:
        raise
    except Exception as e:  # rfc8785 errors, custom serializer errors
        raise PayloadError(f"Payload of type {type(payload).__name__} cannot be serialized to JSON: {e}") from e
