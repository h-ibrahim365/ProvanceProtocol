package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.LedgerEntry;
import io.github.hibrahim365.provance.LedgerUnavailableException;
import io.github.hibrahim365.provance.ProvanceLedger;
import io.github.hibrahim365.provance.store.InMemoryLedgerStore;

import jakarta.servlet.FilterChain;
import jakarta.servlet.http.HttpServletResponse;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.util.Map;
import java.util.Optional;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ProvanceAuditFilterTests {

    private final InMemoryLedgerStore store = new InMemoryLedgerStore();
    private final ProvanceLedger ledger = ProvanceLedger.builder()
            .ledgerId("http-test")
            .secretKey("unit-test-secret-key-0123456789abcdef")
            .store(store)
            .build();

    @AfterEach
    void close() {
        ledger.close();
    }

    private static FilterChain status(int code) {
        return (req, res) -> ((HttpServletResponse) res).setStatus(code);
    }

    private void run(HttpAuditOptions options, FakeHttp.Request request, FakeHttp.Response response, FilterChain chain) throws Exception {
        new ProvanceAuditFilter(ledger, options, 0).doFilter(request.build(), response.build(), chain);
    }

    @SuppressWarnings("unchecked")
    private Map<String, Object> lastPayload() {
        return (Map<String, Object>) store.getHead().orElseThrow().readPayload();
    }

    @Test
    void recordsMethodPathAndStatusButNoPersonalDataByDefault() throws Exception {
        run(new HttpAuditOptions(), new FakeHttp.Request(), new FakeHttp.Response(), status(201));

        LedgerEntry entry = store.getHead().orElseThrow();
        assertEquals(HttpRequestAuditPayload.COMPLETED_EVENT, entry.eventType());
        Map<String, Object> payload = lastPayload();
        assertEquals("POST", payload.get("method"));
        assertEquals("/api/orders", payload.get("path"));
        assertEquals(201L, payload.get("statusCode"));
        assertEquals("completed", payload.get("outcome"));
        assertFalse(entry.payloadJson().contains("SECRET"), entry.payloadJson());
        assertFalse(payload.containsKey("clientIp"));
        assertFalse(payload.containsKey("userAgent"));
        assertFalse(payload.containsKey("actorId"));
        assertTrue(ledger.verify().valid());
    }

    @Test
    void optionalFieldsAreRecordedWhenEnabled() throws Exception {
        HttpAuditOptions options = new HttpAuditOptions().setIncludeQueryString(true).setIncludeClientIp(true).setIncludeUserAgent(true);
        run(options, new FakeHttp.Request(), new FakeHttp.Response(), status(200));

        Map<String, Object> payload = lastPayload();
        assertEquals("token=SECRET", payload.get("queryString"));
        assertEquals("10.1.2.3", payload.get("clientIp"));
        assertEquals("unit-test-agent", payload.get("userAgent"));
    }

    @Test
    void excludedPathsAreMatchedOnWholeSegments() throws Exception {
        for (String uri : new String[] {"/actuator/health", "/HEALTH", "/metrics/jvm", "/app/healthz"}) {
            FakeHttp.Request request = new FakeHttp.Request();
            request.uri = uri;
            request.contextPath = uri.startsWith("/app") ? "/app" : "";
            run(new HttpAuditOptions(), request, new FakeHttp.Response(), status(200));
        }
        assertEquals(0, store.size());

        FakeHttp.Request notExcluded = new FakeHttp.Request();
        notExcluded.uri = "/healthcheck";
        run(new HttpAuditOptions(), notExcluded, new FakeHttp.Response(), status(200));
        assertEquals(1, store.size());
    }

    @Test
    void predicateSelectsTheAuditedRequests() throws Exception {
        HttpAuditOptions options = new HttpAuditOptions().setShouldAudit(r -> r.getRequestURI().startsWith("/api/"));
        FakeHttp.Request other = new FakeHttp.Request();
        other.uri = "/static/app.js";
        run(options, other, new FakeHttp.Response(), status(200));
        run(options, new FakeHttp.Request(), new FakeHttp.Response(), status(200));
        assertEquals(1, store.size());
    }

    @Test
    void unhandledExceptionsAreRecordedAsFailedWith500AndRethrown() {
        IllegalStateException boom = new IllegalStateException("contains personal data");
        FilterChain failing = (req, res) -> {
            throw boom;
        };
        IllegalStateException thrown = assertThrows(IllegalStateException.class,
                () -> run(new HttpAuditOptions(), new FakeHttp.Request(), new FakeHttp.Response(), failing));
        assertEquals(boom, thrown);

        LedgerEntry entry = store.getHead().orElseThrow();
        assertEquals(HttpRequestAuditPayload.FAILED_EVENT, entry.eventType());
        Map<String, Object> payload = lastPayload();
        assertEquals(500L, payload.get("statusCode"));
        assertEquals("java.lang.IllegalStateException", payload.get("exceptionType"));
        assertFalse(entry.payloadJson().contains("personal data"));
    }

    @Test
    void actorAndTraceIdAreRecorded() throws Exception {
        FakeHttp.Request request = new FakeHttp.Request();
        request.principal = () -> "alice";
        request.headers.put("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        run(new HttpAuditOptions(), request, new FakeHttp.Response(), status(204));

        Map<String, Object> payload = lastPayload();
        assertEquals("alice", payload.get("actorId"));
        assertEquals("4bf92f3577b34da6a3ce929d0e0e4736", payload.get("traceId"));
    }

    @Test
    void customActorResolverIsUsed() throws Exception {
        HttpAuditOptions options = new HttpAuditOptions().setActorIdResolver(r -> r.getHeader("X-Service"));
        FakeHttp.Request request = new FakeHttp.Request();
        request.headers.put("X-Service", "billing-batch");
        run(options, request, new FakeHttp.Response(), status(200));
        assertEquals("billing-batch", lastPayload().get("actorId"));
    }

    @Test
    void auditFailureIsLoggedByDefaultAndThrownInThrowMode() throws Exception {
        ledger.close(); // every append now fails

        run(new HttpAuditOptions(), new FakeHttp.Request(), new FakeHttp.Response(), status(200)); // no exception

        HttpAuditOptions strict = new HttpAuditOptions().setFailureMode(HttpAuditFailureMode.THROW);
        assertThrows(LedgerUnavailableException.class,
                () -> run(strict, new FakeHttp.Request(), new FakeHttp.Response(), status(200)));
        assertEquals(Optional.empty(), store.getHead());
    }

    @Test
    void traceparentIsValidatedAndLogValuesAreSanitized() {
        assertNull(ProvanceAuditFilter.traceId(null));
        assertNull(ProvanceAuditFilter.traceId("garbage"));
        assertNull(ProvanceAuditFilter.traceId("00-00000000000000000000000000000000-00f067aa0ba902b7-01"));
        assertNull(ProvanceAuditFilter.traceId("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01"));
        assertEquals("/api/x fake entry", ProvanceAuditFilter.forLog("/api/x\r\n fake entry"));
    }
}
