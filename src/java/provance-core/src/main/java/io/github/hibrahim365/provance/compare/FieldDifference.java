package io.github.hibrahim365.provance.compare;

/**
 * One field that differs between the two copies of an entry.
 *
 * @param field        envelope field ({@code eventType}, {@code timestamp}, ...) or payload path ({@code payload.data.amount})
 * @param archiveValue value in the archive, as JSON for payload fields; {@code null} when absent
 * @param primaryValue value in the primary store; {@code null} when absent
 */
public record FieldDifference(String field, String archiveValue, String primaryValue) {
}
