package io.github.hibrahim365.provance.spring;

import io.github.hibrahim365.provance.LedgerService;
import io.github.hibrahim365.provance.LedgerUnavailableException;

import jakarta.servlet.AsyncEvent;
import jakarta.servlet.AsyncListener;
import jakarta.servlet.DispatcherType;
import jakarta.servlet.Filter;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletRequest;
import jakarta.servlet.ServletResponse;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.core.Ordered;

import java.io.IOException;
import java.lang.System.Logger.Level;
import java.util.Objects;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Records one ledger entry per HTTP request, after the rest of the filter chain has run.
 *
 * <p>Registered automatically when {@code provance.http.enabled=true}. Its default order (0) places it after Spring
 * Security, so the authenticated user is known.
 */
public final class ProvanceAuditFilter implements Filter, Ordered {

    private static final System.Logger LOG = System.getLogger(ProvanceAuditFilter.class.getName());

    private final LedgerService ledger;
    private final HttpAuditOptions options;
    private final int order;

    /** Creates the filter. */
    public ProvanceAuditFilter(LedgerService ledger, HttpAuditOptions options, int order) {
        this.ledger = Objects.requireNonNull(ledger, "ledger");
        this.options = Objects.requireNonNull(options, "options");
        this.order = order;
    }

    @Override
    public int getOrder() {
        return order;
    }

    @Override
    public void doFilter(ServletRequest req, ServletResponse res, FilterChain chain) throws IOException, ServletException {
        if (!(req instanceof HttpServletRequest) || !(res instanceof HttpServletResponse)
                || req.getDispatcherType() != DispatcherType.REQUEST) {
            chain.doFilter(req, res);
            return;
        }
        HttpServletRequest request = (HttpServletRequest) req;
        HttpServletResponse response = (HttpServletResponse) res;
        if (!options.isAudited(request)) {
            chain.doFilter(req, res);
            return;
        }

        long start = System.nanoTime();
        try {
            chain.doFilter(req, res);
        } catch (IOException | ServletException | RuntimeException | Error e) {
            // The original exception always wins: an audit failure here is logged, never thrown.
            audit(request, response, e, start, false);
            throw e;
        }

        if (request.isAsyncStarted()) {
            // Spring MVC async request: the status is known when the async processing completes.
            AtomicBoolean done = new AtomicBoolean();
            request.getAsyncContext().addListener(new AsyncListener() {
                @Override
                public void onComplete(AsyncEvent event) {
                    if (done.compareAndSet(false, true)) {
                        audit(request, response, null, start, false);
                    }
                }

                @Override
                public void onError(AsyncEvent event) {
                    if (done.compareAndSet(false, true)) {
                        Throwable t = event.getThrowable();
                        audit(request, response, t != null ? t : new IllegalStateException("async error"), start, false);
                    }
                }

                @Override
                public void onTimeout(AsyncEvent event) {
                    // completion or error follows
                }

                @Override
                public void onStartAsync(AsyncEvent event) {
                    event.getAsyncContext().addListener(this);
                }
            });
            return;
        }

        audit(request, response, null, start, options.getFailureMode() == HttpAuditFailureMode.THROW);
    }

    private void audit(HttpServletRequest request, HttpServletResponse response, Throwable exception, long start, boolean allowThrow) {
        String eventType = exception == null ? HttpRequestAuditPayload.COMPLETED_EVENT : HttpRequestAuditPayload.FAILED_EVENT;
        int statusCode = exception != null && !response.isCommitted() ? 500 : response.getStatus();

        HttpRequestAuditPayload payload;
        try {
            payload = new HttpRequestAuditPayload(
                    options.actorIdResolver().apply(request),
                    request.getMethod(),
                    request.getRequestURI(),
                    options.isIncludeQueryString() ? request.getQueryString() : null,
                    statusCode,
                    exception == null ? "completed" : "failed",
                    TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start),
                    traceId(request.getHeader("traceparent")),
                    exception == null ? null : exception.getClass().getName(),
                    options.isIncludeClientIp() ? request.getRemoteAddr() : null,
                    options.isIncludeUserAgent() ? request.getHeader("User-Agent") : null);
        } catch (RuntimeException e) {
            fail(request, e, allowThrow);
            return;
        }

        try {
            ledger.addEntryAsync(eventType, payload).get(options.getAuditTimeout().toMillis(), TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            fail(request, e, allowThrow);
        } catch (ExecutionException e) {
            fail(request, e.getCause(), allowThrow);
        } catch (TimeoutException e) {
            fail(request, new LedgerUnavailableException("No audit entry after " + options.getAuditTimeout().toMillis() + " ms.", e),
                    allowThrow);
        } catch (RuntimeException e) {
            fail(request, e, allowThrow);
        }
    }

    private static void fail(HttpServletRequest request, Throwable error, boolean allowThrow) {
        // Method and path come from the client: line breaks are removed so they cannot forge log lines.
        LOG.log(Level.ERROR, "PROVANCE could not record the audit entry for " + forLog(request.getMethod()) + " "
                + forLog(request.getRequestURI()) + ".", error);
        if (allowThrow) {
            if (error instanceof RuntimeException) {
                throw (RuntimeException) error;
            }
            throw new LedgerUnavailableException("PROVANCE could not record the audit entry.", error);
        }
    }

    /** Trace id of a W3C {@code traceparent} header ({@code 00-<trace id>-<span id>-<flags>}), or {@code null}. */
    static String traceId(String traceparent) {
        if (traceparent == null) {
            return null;
        }
        String[] parts = traceparent.trim().split("-");
        if (parts.length < 4 || parts[1].length() != 32) {
            return null;
        }
        for (int i = 0; i < 32; i++) {
            char c = parts[1].charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) {
                return null;
            }
        }
        return parts[1].equals("00000000000000000000000000000000") ? null : parts[1];
    }

    static String forLog(String value) {
        return value == null ? "" : value.replace("\r", "").replace("\n", "");
    }
}
