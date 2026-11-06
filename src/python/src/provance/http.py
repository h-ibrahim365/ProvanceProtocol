"""One ledger entry per HTTP request, for ASGI (FastAPI, Starlette, Django ASGI), WSGI (Flask, Django WSGI) and
Django middleware. Same payload and privacy defaults as the ASP.NET Core middleware and the Spring filter.

The entry is written after the response: it records the status code. Query strings, client IP addresses and user
agents are personal data in many cases and are off by default. Exception messages are never recorded.
"""

from __future__ import annotations

import asyncio
import enum
import logging
import re
import time
from concurrent.futures import TimeoutError as FutureTimeout
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Iterator

from .errors import LedgerUnavailableError
from .ledger import ProvanceLedger

log = logging.getLogger("provance.http")

COMPLETED_EVENT = "HTTP_REQUEST_COMPLETED"
FAILED_EVENT = "HTTP_REQUEST_FAILED"
ABORTED_EVENT = "HTTP_REQUEST_ABORTED"

_TRACEPARENT = re.compile(r"\A[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}\Z")


class HttpAuditFailureMode(enum.Enum):
    LOG_AND_CONTINUE = "log_and_continue"
    """Log the error; the response goes through and the request is not audited."""
    RAISE = "raise"
    """Raise after the response. It does not undo what the handler did."""


@dataclass
class HttpAuditOptions:
    failure_mode: HttpAuditFailureMode = HttpAuditFailureMode.LOG_AND_CONTINUE
    audit_timeout: float = 5.0
    """Maximum seconds a request waits for its audit entry."""
    include_query_string: bool = False
    include_client_ip: bool = False
    include_user_agent: bool = False
    excluded_paths: list[str] = field(default_factory=lambda: ["/health", "/healthz", "/metrics", "/favicon.ico"])
    """Path prefixes never audited, matched on whole segments and ignoring case."""
    should_audit: Callable[[str, str], bool] | None = None
    """Optional ``(method, path) -> bool``. Applied after ``excluded_paths``."""
    actor_id_resolver: Callable[[Any], str | None] | None = None
    """Receives the ASGI scope, the WSGI environ or the Django request. Defaults: the Starlette ``scope["user"]``,
    ``REMOTE_USER``, the Django user's primary key."""

    def is_audited(self, method: str, path: str) -> bool:
        lowered = path.lower()
        for prefix in self.excluded_paths:
            p = prefix.rstrip("/").lower()
            if p and (lowered == p or lowered.startswith(p + "/")):
                return False
        return self.should_audit is None or self.should_audit(method, path)


def trace_id(traceparent: str | None) -> str | None:
    """Trace id of a W3C ``traceparent`` header, or ``None``."""
    if not traceparent:
        return None
    m = _TRACEPARENT.match(traceparent.strip())
    if not m or m.group(1) == "0" * 32:
        return None
    return m.group(1)


def for_log(value: str | None) -> str:
    """Removes line breaks from client supplied values before logging them (log forging)."""
    return (value or "").replace("\r", "").replace("\n", "")


def _payload(actor_id: str | None, method: str, path: str, query: str | None, status: int, outcome: str,
             duration_ms: int, trace: str | None, exception: BaseException | None, client_ip: str | None,
             user_agent: str | None, options: HttpAuditOptions) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "actorId": actor_id,
        "method": method,
        "path": path,
        "queryString": (query or None) if options.include_query_string else None,
        "statusCode": status,
        "outcome": outcome,
        "durationMs": duration_ms,
        "traceId": trace,
        "exceptionType": None if exception is None else type(exception).__qualname__,
        "clientIp": client_ip if options.include_client_ip else None,
        "userAgent": user_agent if options.include_user_agent else None,
    }
    return {k: v for k, v in payload.items() if v is not None}


def _handle_failure(options: HttpAuditOptions, method: str, path: str, error: BaseException, allow_raise: bool) -> None:
    log.error("PROVANCE could not record the audit entry for %s %s: %s", for_log(method), for_log(path), error)
    if allow_raise and options.failure_mode is HttpAuditFailureMode.RAISE:
        if isinstance(error, Exception) and not isinstance(error, (asyncio.TimeoutError, FutureTimeout)):
            raise error
        raise LedgerUnavailableError(f"No audit entry after {options.audit_timeout} s.") from error


# ---------------------------------------------------------------------------------------------------------------------
# ASGI


class ProvanceAsgiMiddleware:
    """ASGI middleware: ``app.add_middleware(ProvanceAsgiMiddleware, ledger=ledger)`` in FastAPI and Starlette.

    Add it after the authentication middleware so ``scope["user"]`` is set when the entry is written.
    """

    def __init__(self, app: Any, ledger: ProvanceLedger, options: HttpAuditOptions | None = None):
        self.app = app
        self.ledger = ledger
        self.options = options or HttpAuditOptions()

    async def __call__(self, scope: dict, receive: Callable, send: Callable) -> None:
        if scope.get("type") != "http":
            await self.app(scope, receive, send)
            return
        method = scope.get("method", "")
        path = scope.get("path", "")
        if not self.options.is_audited(method, path):
            await self.app(scope, receive, send)
            return

        status = {"code": 200, "started": False}

        async def capture(message: dict) -> None:
            if message.get("type") == "http.response.start":
                status["code"] = int(message.get("status", 200))
                status["started"] = True
            await send(message)

        start = time.perf_counter()
        try:
            await self.app(scope, receive, capture)
        except asyncio.CancelledError:
            # Client disconnected: record it without waiting, the task is being cancelled.
            self._submit(scope, ABORTED_EVENT, "aborted", 499, start, None)
            raise
        except BaseException as e:
            code = status["code"] if status["started"] else 500
            await self._audit(scope, FAILED_EVENT, "failed", code, start, e, allow_raise=False)
            raise
        await self._audit(scope, COMPLETED_EVENT, "completed", status["code"], start, None, allow_raise=True)

    def _build(self, scope: dict, outcome: str, status: int, start: float, exception: BaseException | None) -> dict:
        headers = {k.decode("latin-1").lower(): v.decode("latin-1") for k, v in scope.get("headers", [])}
        client = scope.get("client")
        resolver = self.options.actor_id_resolver or _asgi_actor
        return _payload(resolver(scope), scope.get("method", ""), scope.get("root_path", "") + scope.get("path", ""),
                        scope.get("query_string", b"").decode("latin-1"), status, outcome,
                        int((time.perf_counter() - start) * 1000), trace_id(headers.get("traceparent")), exception,
                        client[0] if client else None, headers.get("user-agent"), self.options)

    async def _audit(self, scope: dict, event: str, outcome: str, status: int, start: float,
                     exception: BaseException | None, allow_raise: bool) -> None:
        method, path = scope.get("method", ""), scope.get("path", "")
        try:
            payload = self._build(scope, outcome, status, start, exception)
            await asyncio.wait_for(self.ledger.add_entry_async(event, payload), self.options.audit_timeout)
        except Exception as e:
            _handle_failure(self.options, method, path, e, allow_raise)

    def _submit(self, scope: dict, event: str, outcome: str, status: int, start: float,
                exception: BaseException | None) -> None:
        try:
            self.ledger.submit(event, self._build(scope, outcome, status, start, exception))
        except Exception as e:
            _handle_failure(self.options, scope.get("method", ""), scope.get("path", ""), e, allow_raise=False)


def _asgi_actor(scope: dict) -> str | None:
    user = scope.get("user")
    if user is None or not getattr(user, "is_authenticated", False):
        return None
    for attribute in ("identity", "username", "display_name"):
        try:
            value = getattr(user, attribute)
        except Exception:
            continue
        if value:
            return str(value)
    return None


# ---------------------------------------------------------------------------------------------------------------------
# WSGI


class ProvanceWsgiMiddleware:
    """WSGI middleware: ``app.wsgi_app = ProvanceWsgiMiddleware(app.wsgi_app, ledger)`` in Flask.

    The entry is written when the server closes the response, so the status code is final.
    """

    def __init__(self, app: Callable, ledger: ProvanceLedger, options: HttpAuditOptions | None = None):
        self.app = app
        self.ledger = ledger
        self.options = options or HttpAuditOptions()

    def __call__(self, environ: dict, start_response: Callable) -> Iterable[bytes]:
        method = environ.get("REQUEST_METHOD", "")
        path = environ.get("PATH_INFO", "")
        if not self.options.is_audited(method, path):
            return self.app(environ, start_response)

        state = {"code": 200, "started": False}

        def capture(status_line: str, headers: list, exc_info: Any = None) -> Callable:
            state["code"] = int(status_line.split(" ", 1)[0])
            state["started"] = True
            return start_response(status_line, headers, exc_info) if exc_info else start_response(status_line, headers)

        start = time.perf_counter()
        try:
            result = self.app(environ, capture)
        except BaseException as e:
            self._audit(environ, FAILED_EVENT, "failed", 500, start, e, allow_raise=False)
            raise
        return _AuditedResponse(result, self, environ, state, start)

    def _audit(self, environ: dict, event: str, outcome: str, status: int, start: float,
               exception: BaseException | None, allow_raise: bool) -> None:
        method, path = environ.get("REQUEST_METHOD", ""), environ.get("PATH_INFO", "")
        try:
            resolver = self.options.actor_id_resolver or (lambda env: env.get("REMOTE_USER") or None)
            payload = _payload(resolver(environ), method, environ.get("SCRIPT_NAME", "") + path,
                               environ.get("QUERY_STRING"), status, outcome, int((time.perf_counter() - start) * 1000),
                               trace_id(environ.get("HTTP_TRACEPARENT")), exception, environ.get("REMOTE_ADDR"),
                               environ.get("HTTP_USER_AGENT"), self.options)
            self.ledger.add_entry(event, payload, timeout=self.options.audit_timeout)
        except Exception as e:
            _handle_failure(self.options, method, path, e, allow_raise)


class _AuditedResponse:
    def __init__(self, inner: Iterable[bytes], middleware: ProvanceWsgiMiddleware, environ: dict, state: dict,
                 start: float):
        self._inner = inner
        self._middleware = middleware
        self._environ = environ
        self._state = state
        self._start = start
        self._error: BaseException | None = None
        self._done = False

    def __iter__(self) -> Iterator[bytes]:
        try:
            yield from self._inner
        except BaseException as e:
            self._error = e
            raise

    def close(self) -> None:
        try:
            if hasattr(self._inner, "close"):
                self._inner.close()
        finally:
            if not self._done:
                self._done = True
                if self._error is None:
                    self._middleware._audit(self._environ, COMPLETED_EVENT, "completed", self._state["code"],
                                            self._start, None, allow_raise=True)
                else:
                    code = self._state["code"] if self._state["started"] else 500
                    self._middleware._audit(self._environ, FAILED_EVENT, "failed", code, self._start, self._error,
                                            allow_raise=False)


# ---------------------------------------------------------------------------------------------------------------------
# Django


def django_middleware(ledger: ProvanceLedger, options: HttpAuditOptions | None = None) -> Callable:
    """Django middleware factory. In ``myapp/audit.py``::

        audit_middleware = django_middleware(ledger)

    then add ``"myapp.audit.audit_middleware"`` to ``MIDDLEWARE``, after ``AuthenticationMiddleware``.
    The actor is the primary key of ``request.user``.
    """
    opts = options or HttpAuditOptions()

    def factory(get_response: Callable) -> Callable:
        return _DjangoMiddleware(get_response, ledger, opts)

    return factory


class _DjangoMiddleware:
    def __init__(self, get_response: Callable, ledger: ProvanceLedger, options: HttpAuditOptions):
        self.get_response = get_response
        self.ledger = ledger
        self.options = options

    def __call__(self, request: Any) -> Any:
        method, path = request.method, request.path_info
        if not self.options.is_audited(method, path):
            return self.get_response(request)
        start = time.perf_counter()
        try:
            response = self.get_response(request)
        except BaseException as e:
            self._audit(request, FAILED_EVENT, "failed", 500, start, e, allow_raise=False)
            raise
        exception = getattr(request, "_provance_exception", None)
        if exception is not None:
            self._audit(request, FAILED_EVENT, "failed", response.status_code, start, exception, allow_raise=False)
        else:
            self._audit(request, COMPLETED_EVENT, "completed", response.status_code, start, None, allow_raise=True)
        return response

    def process_exception(self, request: Any, exception: BaseException) -> None:
        request._provance_exception = exception  # Django turns it into a 500 response; keep the type
        return None

    def _audit(self, request: Any, event: str, outcome: str, status: int, start: float,
               exception: BaseException | None, allow_raise: bool) -> None:
        try:
            meta = request.META
            resolver = self.options.actor_id_resolver or _django_actor
            payload = _payload(resolver(request), request.method, request.path, meta.get("QUERY_STRING"), status,
                               outcome, int((time.perf_counter() - start) * 1000), trace_id(meta.get("HTTP_TRACEPARENT")),
                               exception, meta.get("REMOTE_ADDR"), meta.get("HTTP_USER_AGENT"), self.options)
            self.ledger.add_entry(event, payload, timeout=self.options.audit_timeout)
        except Exception as e:
            _handle_failure(self.options, request.method, request.path, e, allow_raise)


def _django_actor(request: Any) -> str | None:
    user = getattr(request, "user", None)
    if user is None or not getattr(user, "is_authenticated", False):
        return None
    return str(getattr(user, "pk", None) or getattr(user, "username", "")) or None
