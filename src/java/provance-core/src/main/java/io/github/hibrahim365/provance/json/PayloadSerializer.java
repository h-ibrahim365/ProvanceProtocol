package io.github.hibrahim365.provance.json;

/**
 * Turns a payload object into JSON text before canonicalization.
 *
 * <p>The default is {@link SimplePayloadSerializer}, which needs no dependency. Plug in Jackson, Gson or
 * any other library with a lambda, for example {@code payload -> objectMapper.writeValueAsString(payload)}.
 * The output is canonicalized afterwards, so formatting and member order do not matter.
 */
@FunctionalInterface
public interface PayloadSerializer {

    /** Serializes {@code payload} (never {@code null}) to JSON text. */
    String toJson(Object payload);
}
