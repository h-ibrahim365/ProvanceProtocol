package io.github.hibrahim365.provance.spring;

/**
 * Changes the HTTP audit options in code, for example to resolve the actor from a JWT claim or to audit only
 * some requests:
 *
 * <pre>{@code
 * @Bean
 * HttpAuditCustomizer auditOnlyApi() {
 *     return options -> options.setShouldAudit(request -> request.getRequestURI().startsWith("/api/"));
 * }
 * }</pre>
 */
@FunctionalInterface
public interface HttpAuditCustomizer {

    /** Customizes the options before the filter is created. */
    void customize(HttpAuditOptions options);
}
