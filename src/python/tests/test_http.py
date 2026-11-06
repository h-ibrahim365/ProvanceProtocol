"""HTTP middleware: ASGI, WSGI and Django, without installing a framework."""

from __future__ import annotations

import asyncio
from types import SimpleNamespace

import pytest

from provance import InMemoryLedgerStore, LedgerStoreError, LedgerUnavailableError, ProvanceLedger
from provance.http import (
    ABORTED_EVENT,
    COMPLETED_EVENT,
    FAILED_EVENT,
    HttpAuditFailureMode,
    HttpAuditOptions,
    ProvanceAsgiMiddleware,
    ProvanceWsgiMiddleware,
    django_middleware,
    for_log,
    trace_id,
)

from conftest import LEDGER_ID, SECRET

TRACEPARENT = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"


@pytest.fixture
def ledger():
    with ProvanceLedger(ledger_id=LEDGER_ID, secret_key=SECRET, store=InMemoryLedgerStore()) as instance:
        yield instance


def entries(ledger):
    return [(e.event_type, e.payload()) for e in ledger.store.read()]


class BrokenStore(InMemoryLedgerStore):
    def append(self, entries):
        raise LedgerStoreError("database down")


# --- helpers ---------------------------------------------------------------------------------------------------------


def test_trace_id_and_log_sanitizing():
    assert trace_id(TRACEPARENT) == "4bf92f3577b34da6a3ce929d0e0e4736"
    assert trace_id("00-" + "0" * 32 + "-00f067aa0ba902b7-01") is None
    assert trace_id("garbage") is None and trace_id(None) is None
    assert for_log("a\r\nFAKE LOG LINE") == "aFAKE LOG LINE"


def test_excluded_paths_match_whole_segments_ignoring_case():
    options = HttpAuditOptions(should_audit=lambda method, path: method != "OPTIONS")
    assert not options.is_audited("GET", "/health")
    assert not options.is_audited("GET", "/HEALTH/live")
    assert options.is_audited("GET", "/healthcheck")
    assert not options.is_audited("OPTIONS", "/api")


# --- ASGI ------------------------------------------------------------------------------------------------------------


def asgi_scope(path="/api/orders", method="POST", user=None, query=b"secret=1"):
    scope = {"type": "http", "method": method, "path": path, "root_path": "", "query_string": query,
             "headers": [(b"traceparent", TRACEPARENT.encode()), (b"user-agent", b"tests")],
             "client": ("10.0.0.1", 5000)}
    if user is not None:
        scope["user"] = user
    return scope


def run_asgi(middleware, scope):
    sent = []

    async def receive():
        return {"type": "http.request", "body": b""}

    async def send(message):
        sent.append(message)

    asyncio.run(middleware(scope, receive, send))
    return sent


async def ok_app(scope, receive, send, status=201):
    await send({"type": "http.response.start", "status": status, "headers": []})
    await send({"type": "http.response.body", "body": b"ok"})


def test_asgi_records_one_entry_per_request_without_personal_data(ledger):
    user = SimpleNamespace(is_authenticated=True, identity="user-42")
    run_asgi(ProvanceAsgiMiddleware(ok_app, ledger), asgi_scope(user=user))
    assert entries(ledger) == [(COMPLETED_EVENT, {
        "actorId": "user-42", "method": "POST", "path": "/api/orders", "statusCode": 201, "outcome": "completed",
        "durationMs": entries(ledger)[0][1]["durationMs"], "traceId": "4bf92f3577b34da6a3ce929d0e0e4736"})]


def test_asgi_optional_fields_and_exclusions(ledger):
    options = HttpAuditOptions(include_query_string=True, include_client_ip=True, include_user_agent=True)
    middleware = ProvanceAsgiMiddleware(ok_app, ledger, options)
    run_asgi(middleware, asgi_scope())
    run_asgi(middleware, asgi_scope(path="/health"))
    (event, payload), = entries(ledger)
    assert payload["queryString"] == "secret=1"
    assert payload["clientIp"] == "10.0.0.1"
    assert payload["userAgent"] == "tests"
    assert "actorId" not in payload


def test_asgi_records_failures_without_the_message(ledger):
    async def failing(scope, receive, send):
        raise RuntimeError("card 4111 1111 1111 1111 declined")

    with pytest.raises(RuntimeError):
        run_asgi(ProvanceAsgiMiddleware(failing, ledger), asgi_scope())
    (event, payload), = entries(ledger)
    assert event == FAILED_EVENT
    assert payload["statusCode"] == 500 and payload["exceptionType"] == "RuntimeError"
    assert "4111" not in str(payload)


def test_asgi_records_aborted_requests(ledger):
    async def slow(scope, receive, send):
        await asyncio.sleep(10)

    async def scenario():
        middleware = ProvanceAsgiMiddleware(slow, ledger)

        async def receive():
            return {"type": "http.request"}

        async def send(message):
            pass

        task = asyncio.create_task(middleware(asgi_scope(), receive, send))
        await asyncio.sleep(0.05)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(scenario())
    ledger.close()  # writes the entry submitted during cancellation
    (event, payload), = entries(ledger)
    assert event == ABORTED_EVENT and payload["statusCode"] == 499


def test_asgi_failure_modes():
    with ProvanceLedger(ledger_id=LEDGER_ID, secret_key=SECRET, store=BrokenStore(), max_append_attempts=1) as broken:
        sent = run_asgi(ProvanceAsgiMiddleware(ok_app, broken), asgi_scope())
        assert sent[0]["status"] == 201  # logged, request unaffected
        with pytest.raises(LedgerUnavailableError):
            run_asgi(ProvanceAsgiMiddleware(ok_app, broken, HttpAuditOptions(failure_mode=HttpAuditFailureMode.RAISE)),
                     asgi_scope())


def test_asgi_passes_other_scopes_through(ledger):
    calls = []

    async def app(scope, receive, send):
        calls.append(scope["type"])

    asyncio.run(ProvanceAsgiMiddleware(app, ledger)({"type": "lifespan"}, None, None))
    assert calls == ["lifespan"] and entries(ledger) == []


# --- WSGI ------------------------------------------------------------------------------------------------------------


def wsgi_environ(path="/api/orders", user=None):
    environ = {"REQUEST_METHOD": "DELETE", "PATH_INFO": path, "SCRIPT_NAME": "/app", "QUERY_STRING": "secret=1",
               "HTTP_TRACEPARENT": TRACEPARENT, "REMOTE_ADDR": "10.0.0.2"}
    if user:
        environ["REMOTE_USER"] = user
    return environ


def call_wsgi(app, environ):
    statuses = []
    response = app(environ, lambda status, headers, exc_info=None: statuses.append(status))
    try:
        body = b"".join(response)
    finally:
        if hasattr(response, "close"):
            response.close()
    return statuses, body


def test_wsgi_records_the_final_status(ledger):
    def app(environ, start_response):
        start_response("204 No Content", [])
        return [b""]

    statuses, _ = call_wsgi(ProvanceWsgiMiddleware(app, ledger), wsgi_environ(user="alice"))
    assert statuses == ["204 No Content"]
    (event, payload), = entries(ledger)
    assert event == COMPLETED_EVENT
    assert payload["actorId"] == "alice" and payload["path"] == "/app/api/orders" and payload["statusCode"] == 204
    assert "queryString" not in payload and "clientIp" not in payload


def test_wsgi_records_failures_while_streaming(ledger):
    def app(environ, start_response):
        start_response("200 OK", [])

        def body():
            yield b"partial"
            raise ValueError("boom")

        return body()

    with pytest.raises(ValueError):
        call_wsgi(ProvanceWsgiMiddleware(app, ledger), wsgi_environ())
    (event, payload), = entries(ledger)
    assert event == FAILED_EVENT and payload["statusCode"] == 200 and payload["exceptionType"] == "ValueError"


def test_wsgi_records_failures_before_the_response(ledger):
    def app(environ, start_response):
        raise KeyError("missing")

    with pytest.raises(KeyError):
        call_wsgi(ProvanceWsgiMiddleware(app, ledger), wsgi_environ())
    assert entries(ledger)[0][0] == FAILED_EVENT


# --- Django ----------------------------------------------------------------------------------------------------------


def django_request(path="/api/orders", user_pk=None):
    user = SimpleNamespace(is_authenticated=user_pk is not None, pk=user_pk)
    return SimpleNamespace(method="PUT", path=path, path_info=path, user=user,
                           META={"QUERY_STRING": "", "HTTP_TRACEPARENT": TRACEPARENT, "REMOTE_ADDR": "10.0.0.3"})


def test_django_middleware(ledger):
    middleware = django_middleware(ledger)(lambda request: SimpleNamespace(status_code=200))
    assert middleware(django_request(user_pk=7)).status_code == 200
    middleware(django_request(path="/healthz"))
    (event, payload), = entries(ledger)
    assert event == COMPLETED_EVENT and payload["actorId"] == "7" and payload["method"] == "PUT"


def test_django_view_exception_turned_into_500(ledger):
    instance = None

    def get_response(request):
        instance.process_exception(request, PermissionError("secret"))  # what Django does for view errors
        return SimpleNamespace(status_code=500)

    instance = django_middleware(ledger)(get_response)
    instance(django_request())
    (event, payload), = entries(ledger)
    assert event == FAILED_EVENT and payload["exceptionType"] == "PermissionError" and payload["statusCode"] == 500
