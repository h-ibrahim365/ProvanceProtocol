package io.github.hibrahim365.provance.json;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Minimal JSON reader, enough to read payloads and checkpoints without a JSON library.
 */
public final class Json {

    private Json() {
    }

    /**
     * Parses JSON text into plain Java values: {@code Map<String, Object>} (insertion ordered),
     * {@code List<Object>}, {@code String}, {@code Long} (integer literals that fit), {@code Double}
     * (other numbers), {@code Boolean} and {@code null}.
     *
     * @throws JsonFormatException the input is not valid JSON or has duplicate member names
     */
    public static Object parse(String json) {
        return convert(JsonParser.parse(json));
    }

    /** Parses a JSON object. */
    @SuppressWarnings("unchecked")
    public static Map<String, Object> parseObject(String json) {
        Object value = parse(json);
        if (!(value instanceof Map)) {
            throw new JsonFormatException("Expected a JSON object.");
        }
        return (Map<String, Object>) value;
    }

    @SuppressWarnings("unchecked")
    private static Object convert(Object node) {
        if (node instanceof Map) {
            Map<String, Object> source = (Map<String, Object>) node;
            Map<String, Object> result = new LinkedHashMap<>(source.size() * 2);
            for (Map.Entry<String, Object> e : source.entrySet()) {
                result.put(e.getKey(), convert(e.getValue()));
            }
            return result;
        }
        if (node instanceof List) {
            List<Object> source = (List<Object>) node;
            List<Object> result = new ArrayList<>(source.size());
            for (Object item : source) {
                result.add(convert(item));
            }
            return result;
        }
        if (node instanceof JsonParser.JsonNumber) {
            String literal = ((JsonParser.JsonNumber) node).literal;
            if (literal.indexOf('.') < 0 && literal.indexOf('e') < 0 && literal.indexOf('E') < 0) {
                try {
                    return Long.parseLong(literal);
                } catch (NumberFormatException ignored) {
                    // too large for a long: falls back to double like any other number
                }
            }
            return Double.parseDouble(literal);
        }
        return node;
    }
}
