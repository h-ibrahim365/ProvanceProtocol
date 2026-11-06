package io.github.hibrahim365.provance;

import java.util.Map;

/**
 * A general purpose payload shape. Any serializable object can be used as a payload; this record is only
 * a convenient default. It serializes to the same JSON as the .NET {@code AuditedPayload}.
 *
 * <p>Keep personal data out of payloads when you can: the ledger is append only. Store money amounts,
 * large integers and precise decimals in {@code data} as strings (RFC 8785 number rules).
 *
 * @param actorId     identifier of the user or system that performed the action
 * @param actorRole   role or kind of actor, for readability
 * @param description short description of the action
 * @param data        free form, application specific data
 */
public record AuditedPayload(String actorId, String actorRole, String description, Map<String, Object> data) {

    /** Payload with an actor and a description only. */
    public static AuditedPayload of(String actorId, String description) {
        return new AuditedPayload(actorId, null, description, null);
    }

    /** Payload with an actor, a description and data. */
    public static AuditedPayload of(String actorId, String description, Map<String, Object> data) {
        return new AuditedPayload(actorId, null, description, data);
    }
}
