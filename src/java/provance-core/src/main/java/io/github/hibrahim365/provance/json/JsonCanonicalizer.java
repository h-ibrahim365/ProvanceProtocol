package io.github.hibrahim365.provance.json;

import java.math.BigDecimal;
import java.math.MathContext;
import java.math.RoundingMode;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * JSON Canonicalization Scheme (JCS) as defined by RFC 8785.
 *
 * <p>The output is the same in every language that implements RFC 8785, which is what makes PROVANCE
 * hashes reproducible outside of Java. Rules applied: object members sorted by UTF-16 code units, no
 * whitespace, strings escaped with the minimal ECMAScript escaping, numbers formatted as IEEE 754
 * doubles using the ECMAScript Number serialization. Integers above 2^53 and decimals with more than
 * about 15 significant digits lose precision: store such values as strings in your payloads.
 */
public final class JsonCanonicalizer {

    private JsonCanonicalizer() {
    }

    /**
     * Canonicalizes a JSON document.
     *
     * @throws JsonFormatException the input is not valid JSON, contains duplicate member names, a non finite
     *                             number or a lone surrogate
     */
    public static String canonicalize(String json) {
        Object root = JsonParser.parse(json);
        StringBuilder sb = new StringBuilder(json.length());
        write(root, sb);
        return sb.toString();
    }

    /**
     * Serializes {@code value} with {@code serializer}, then canonicalizes the result.
     *
     * @throws JsonFormatException the serializer output is not valid canonicalizable JSON
     */
    public static String serialize(Object value, PayloadSerializer serializer) {
        if (value == null) {
            throw new IllegalArgumentException("value is required");
        }
        String json = serializer.toJson(value);
        if (json == null) {
            throw new JsonFormatException("The payload serializer returned null for " + value.getClass().getName() + ".");
        }
        return canonicalize(json);
    }

    @SuppressWarnings("unchecked")
    private static void write(Object value, StringBuilder sb) {
        if (value == null) {
            sb.append("null");
        } else if (value instanceof Map) {
            writeObject((Map<String, Object>) value, sb);
        } else if (value instanceof List) {
            sb.append('[');
            boolean first = true;
            for (Object item : (List<Object>) value) {
                if (!first) sb.append(',');
                first = false;
                write(item, sb);
            }
            sb.append(']');
        } else if (value instanceof String) {
            writeString((String) value, sb);
        } else if (value instanceof JsonParser.JsonNumber) {
            sb.append(formatNumber(((JsonParser.JsonNumber) value).literal));
        } else if (value instanceof Boolean) {
            sb.append(((Boolean) value) ? "true" : "false");
        } else {
            throw new JsonFormatException("Unsupported JSON value of type " + value.getClass().getName() + ".");
        }
    }

    private static void writeObject(Map<String, Object> members, StringBuilder sb) {
        // RFC 8785 section 3.2.3: sort by UTF-16 code units, which is exactly String.compareTo in Java.
        List<String> names = new ArrayList<>(members.keySet());
        names.sort(String::compareTo);
        sb.append('{');
        for (int i = 0; i < names.size(); i++) {
            if (i > 0) sb.append(',');
            String name = names.get(i);
            writeString(name, sb);
            sb.append(':');
            write(members.get(name), sb);
        }
        sb.append('}');
    }

    /**
     * Appends {@code value} as a canonical JSON string literal (RFC 8785 escaping).
     *
     * @throws JsonFormatException the string contains a lone surrogate
     */
    public static void writeString(String value, StringBuilder sb) {
        sb.append('"');
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) {
                        sb.append("\\u00");
                        sb.append(Character.forDigit(c >> 4, 16)).append(Character.forDigit(c & 0xF, 16));
                    } else if (Character.isSurrogate(c)) {
                        if (Character.isHighSurrogate(c) && i + 1 < value.length() && Character.isLowSurrogate(value.charAt(i + 1))) {
                            sb.append(c).append(value.charAt(i + 1));
                            i++;
                        } else {
                            throw new JsonFormatException("Strings must be valid Unicode (lone surrogate found).");
                        }
                    } else {
                        sb.append(c);
                    }
                    break;
            }
        }
        sb.append('"');
    }

    /** Formats a JSON number literal the way ECMAScript {@code Number.prototype.toString()} does (RFC 8785 section 3.2.2.3). */
    static String formatNumber(String literal) {
        double value;
        try {
            value = Double.parseDouble(literal);
        } catch (NumberFormatException e) {
            throw new JsonFormatException("Number '" + literal + "' is not a valid number.", e);
        }
        if (!Double.isFinite(value)) {
            throw new JsonFormatException("Number '" + literal + "' cannot be represented as a finite IEEE 754 double.");
        }
        return formatDouble(value);
    }

    /** ECMAScript {@code Number.prototype.toString()} of a finite double. */
    static String formatDouble(double value) {
        if (!Double.isFinite(value)) {
            throw new JsonFormatException("NaN and Infinity are not valid JSON numbers.");
        }
        if (value == 0) {
            return "0"; // also covers negative zero
        }

        boolean negative = value < 0;
        double abs = Math.abs(value);

        // Shortest decimal that round-trips, closest to the exact value, ties to even (ECMAScript 7.1.12.1).
        // Double.toString is not guaranteed to be the shortest before Java 19, so the digits are searched explicitly.
        BigDecimal exact = new BigDecimal(abs);
        String digits = null;
        int n = 0;
        for (int precision = 1; precision <= 17 && digits == null; precision++) {
            BigDecimal down = exact.round(new MathContext(precision, RoundingMode.FLOOR));
            BigDecimal up = exact.round(new MathContext(precision, RoundingMode.CEILING));
            boolean downOk = roundTrips(down, abs);
            boolean upOk = roundTrips(up, abs);
            BigDecimal chosen;
            if (downOk && upOk) {
                int cmp = exact.subtract(down).compareTo(up.subtract(exact));
                if (cmp < 0) {
                    chosen = down;
                } else if (cmp > 0) {
                    chosen = up;
                } else {
                    chosen = down.unscaledValue().testBit(0) ? up : down;
                }
            } else if (downOk) {
                chosen = down;
            } else if (upOk) {
                chosen = up;
            } else {
                continue;
            }
            BigDecimal stripped = chosen.stripTrailingZeros();
            digits = stripped.unscaledValue().toString();
            // value = digits * 10^(-scale) = digits * 10^(n - k)
            n = digits.length() - stripped.scale();
        }
        if (digits == null) {
            throw new IllegalStateException("No round-tripping representation found for " + value);
        }

        int k = digits.length();
        String result;
        if (k <= n && n <= 21) {
            result = digits + "0".repeat(n - k);
        } else if (0 < n && n <= 21) {
            result = digits.substring(0, n) + "." + digits.substring(n);
        } else if (-6 < n && n <= 0) {
            result = "0." + "0".repeat(-n) + digits;
        } else {
            int e = n - 1;
            String exponent = (e < 0 ? "-" : "+") + Math.abs(e);
            result = k == 1
                    ? digits + "e" + exponent
                    : digits.charAt(0) + "." + digits.substring(1) + "e" + exponent;
        }
        return negative ? "-" + result : result;
    }

    private static boolean roundTrips(BigDecimal candidate, double target) {
        return Double.parseDouble(candidate.toString()) == target;
    }
}
