package io.github.hibrahim365.provance.spring;

import jakarta.servlet.http.HttpServletRequest;

import java.security.Principal;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Objects;
import java.util.function.Function;
import java.util.function.Predicate;

/**
 * Runtime options of {@link ProvanceAuditFilter}: the {@code provance.http.*} properties plus what can only be set in
 * code (a filter predicate and the actor resolver). Change them with an {@link HttpAuditCustomizer} bean.
 */
public final class HttpAuditOptions {

    private HttpAuditFailureMode failureMode = HttpAuditFailureMode.LOG_AND_CONTINUE;
    private Duration auditTimeout = Duration.ofSeconds(5);
    private boolean includeQueryString;
    private boolean includeClientIp;
    private boolean includeUserAgent;
    private List<String> excludedPaths = new ArrayList<>(List.of("/actuator", "/health", "/healthz", "/metrics", "/favicon.ico"));
    private Predicate<HttpServletRequest> shouldAudit;
    private Function<HttpServletRequest, String> actorIdResolver = HttpAuditOptions::defaultActorId;

    /** Options from the {@code provance.http.*} properties. */
    public static HttpAuditOptions from(ProvanceProperties.Http properties) {
        HttpAuditOptions o = new HttpAuditOptions();
        o.failureMode = properties.getFailureMode();
        o.auditTimeout = properties.getAuditTimeout();
        o.includeQueryString = properties.isIncludeQueryString();
        o.includeClientIp = properties.isIncludeClientIp();
        o.includeUserAgent = properties.isIncludeUserAgent();
        o.excludedPaths = new ArrayList<>(properties.getExcludedPaths());
        return o;
    }

    public HttpAuditFailureMode getFailureMode() {
        return failureMode;
    }

    public HttpAuditOptions setFailureMode(HttpAuditFailureMode failureMode) {
        this.failureMode = Objects.requireNonNull(failureMode, "failureMode");
        return this;
    }

    public Duration getAuditTimeout() {
        return auditTimeout;
    }

    public HttpAuditOptions setAuditTimeout(Duration auditTimeout) {
        this.auditTimeout = Objects.requireNonNull(auditTimeout, "auditTimeout");
        return this;
    }

    public boolean isIncludeQueryString() {
        return includeQueryString;
    }

    public HttpAuditOptions setIncludeQueryString(boolean includeQueryString) {
        this.includeQueryString = includeQueryString;
        return this;
    }

    public boolean isIncludeClientIp() {
        return includeClientIp;
    }

    public HttpAuditOptions setIncludeClientIp(boolean includeClientIp) {
        this.includeClientIp = includeClientIp;
        return this;
    }

    public boolean isIncludeUserAgent() {
        return includeUserAgent;
    }

    public HttpAuditOptions setIncludeUserAgent(boolean includeUserAgent) {
        this.includeUserAgent = includeUserAgent;
        return this;
    }

    /** Path prefixes that are never audited, matched on whole segments and ignoring case. Mutable. */
    public List<String> getExcludedPaths() {
        return excludedPaths;
    }

    /**
     * Optional predicate: when set, a request is audited only if it returns {@code true} (after the excluded paths).
     */
    public HttpAuditOptions setShouldAudit(Predicate<HttpServletRequest> shouldAudit) {
        this.shouldAudit = shouldAudit;
        return this;
    }

    /** Resolves the actor id. Default: the name of {@link HttpServletRequest#getUserPrincipal()}, {@code null} for anonymous. */
    public HttpAuditOptions setActorIdResolver(Function<HttpServletRequest, String> actorIdResolver) {
        this.actorIdResolver = Objects.requireNonNull(actorIdResolver, "actorIdResolver");
        return this;
    }

    Function<HttpServletRequest, String> actorIdResolver() {
        return actorIdResolver;
    }

    boolean isAudited(HttpServletRequest request) {
        String path = pathWithinApplication(request);
        for (String prefix : excludedPaths) {
            if (startsWithSegments(path, prefix)) {
                return false;
            }
        }
        return shouldAudit == null || shouldAudit.test(request);
    }

    static String pathWithinApplication(HttpServletRequest request) {
        String uri = request.getRequestURI() == null ? "" : request.getRequestURI();
        String context = request.getContextPath() == null ? "" : request.getContextPath();
        return !context.isEmpty() && uri.startsWith(context) ? uri.substring(context.length()) : uri;
    }

    static boolean startsWithSegments(String path, String prefix) {
        if (prefix == null || prefix.isEmpty()) {
            return false;
        }
        String p = prefix.endsWith("/") ? prefix.substring(0, prefix.length() - 1) : prefix;
        String a = path.toLowerCase(Locale.ROOT);
        String b = p.toLowerCase(Locale.ROOT);
        return a.equals(b) || a.startsWith(b + "/");
    }

    private static String defaultActorId(HttpServletRequest request) {
        Principal principal = request.getUserPrincipal();
        return principal == null ? null : principal.getName();
    }
}
