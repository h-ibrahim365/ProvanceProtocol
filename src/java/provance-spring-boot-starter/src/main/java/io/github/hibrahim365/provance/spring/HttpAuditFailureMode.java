package io.github.hibrahim365.provance.spring;

/** What the HTTP audit filter does when the audit entry cannot be written. */
public enum HttpAuditFailureMode {

    /** Log the error and let the response go through. The request is not audited. */
    LOG_AND_CONTINUE,

    /**
     * Throw, so the request ends with an error when the response has not been committed yet. The audit runs after
     * the controller: this does not undo what the controller already did.
     */
    THROW
}
