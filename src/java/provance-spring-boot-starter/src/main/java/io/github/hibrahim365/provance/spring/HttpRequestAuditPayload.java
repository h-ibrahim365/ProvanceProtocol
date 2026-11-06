package io.github.hibrahim365.provance.spring;

/**
 * Payload recorded by the HTTP audit filter for each audited request. Same JSON fields as the .NET middleware.
 *
 * @param actorId       authenticated actor id, or {@code null} for anonymous requests
 * @param method        HTTP method
 * @param path          request path (context path included), without the query string
 * @param queryString   query string, only when enabled
 * @param statusCode    response status code; 500 when an exception escaped before the response was committed
 * @param outcome       {@code completed} or {@code failed}
 * @param durationMs    time spent in the rest of the filter chain, in milliseconds
 * @param traceId       W3C trace id of the request ({@code traceparent} header), to correlate with logs
 * @param exceptionType class name of the unhandled exception. The message is not recorded, it may contain personal data.
 * @param clientIp      client IP, only when enabled
 * @param userAgent     User-Agent header, only when enabled
 */
public record HttpRequestAuditPayload(
        String actorId,
        String method,
        String path,
        String queryString,
        int statusCode,
        String outcome,
        long durationMs,
        String traceId,
        String exceptionType,
        String clientIp,
        String userAgent) {

    /** Event type of a request that completed, whatever its status code. */
    public static final String COMPLETED_EVENT = "HTTP_REQUEST_COMPLETED";

    /** Event type of a request that ended with an unhandled exception. */
    public static final String FAILED_EVENT = "HTTP_REQUEST_FAILED";
}
