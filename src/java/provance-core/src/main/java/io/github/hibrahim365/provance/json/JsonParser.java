package io.github.hibrahim365.provance.json;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Strict RFC 8259 parser. Objects become {@link LinkedHashMap}, arrays {@link ArrayList}, numbers
 * {@link JsonNumber} (the literal is kept so the canonicalizer decides how to format it).
 * Duplicate member names, comments, trailing commas and trailing content are rejected.
 */
final class JsonParser {

    static final int MAX_DEPTH = 64;

    private final String text;
    private int pos;

    private JsonParser(String text) {
        this.text = text;
    }

    /** A JSON number, kept as its literal text. */
    static final class JsonNumber {
        final String literal;

        JsonNumber(String literal) {
            this.literal = literal;
        }

        @Override
        public String toString() {
            return literal;
        }
    }

    static Object parse(String text) {
        if (text == null) {
            throw new JsonFormatException("JSON input is null.");
        }
        JsonParser parser = new JsonParser(text);
        parser.skipWhitespace();
        Object value = parser.readValue(0);
        parser.skipWhitespace();
        if (parser.pos != text.length()) {
            throw parser.error("Unexpected content after the JSON value");
        }
        return value;
    }

    private Object readValue(int depth) {
        if (pos >= text.length()) {
            throw error("Unexpected end of input");
        }
        char c = text.charAt(pos);
        switch (c) {
            case '{':
                return readObject(depth + 1);
            case '[':
                return readArray(depth + 1);
            case '"':
                return readString();
            case 't':
                expectLiteral("true");
                return Boolean.TRUE;
            case 'f':
                expectLiteral("false");
                return Boolean.FALSE;
            case 'n':
                expectLiteral("null");
                return null;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) {
                    return readNumber();
                }
                throw error("Unexpected character '" + printable(c) + "'");
        }
    }

    private Map<String, Object> readObject(int depth) {
        checkDepth(depth);
        pos++; // '{'
        Map<String, Object> members = new LinkedHashMap<>();
        skipWhitespace();
        if (peek() == '}') {
            pos++;
            return members;
        }
        while (true) {
            skipWhitespace();
            if (peek() != '"') {
                throw error("Expected a member name");
            }
            String name = readString();
            skipWhitespace();
            if (peek() != ':') {
                throw error("Expected ':'");
            }
            pos++;
            skipWhitespace();
            Object value = readValue(depth);
            if (members.containsKey(name)) {
                throw new JsonFormatException("Duplicate member name '" + name + "' is not allowed.");
            }
            members.put(name, value);
            skipWhitespace();
            char c = peek();
            if (c == ',') {
                pos++;
                continue;
            }
            if (c == '}') {
                pos++;
                return members;
            }
            throw error("Expected ',' or '}'");
        }
    }

    private List<Object> readArray(int depth) {
        checkDepth(depth);
        pos++; // '['
        List<Object> items = new ArrayList<>();
        skipWhitespace();
        if (peek() == ']') {
            pos++;
            return items;
        }
        while (true) {
            skipWhitespace();
            items.add(readValue(depth));
            skipWhitespace();
            char c = peek();
            if (c == ',') {
                pos++;
                continue;
            }
            if (c == ']') {
                pos++;
                return items;
            }
            throw error("Expected ',' or ']'");
        }
    }

    private String readString() {
        pos++; // opening quote
        StringBuilder sb = null;
        int start = pos;
        while (true) {
            if (pos >= text.length()) {
                throw error("Unterminated string");
            }
            char c = text.charAt(pos);
            if (c == '"') {
                String value = sb == null ? text.substring(start, pos) : sb.append(text, start, pos).toString();
                pos++;
                return value;
            }
            if (c < 0x20) {
                throw error("Control character in string");
            }
            if (c == '\\') {
                if (sb == null) {
                    sb = new StringBuilder();
                }
                sb.append(text, start, pos);
                pos++;
                if (pos >= text.length()) {
                    throw error("Unterminated escape sequence");
                }
                char e = text.charAt(pos);
                switch (e) {
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    case 'b': sb.append('\b'); break;
                    case 'f': sb.append('\f'); break;
                    case 'n': sb.append('\n'); break;
                    case 'r': sb.append('\r'); break;
                    case 't': sb.append('\t'); break;
                    case 'u':
                        if (pos + 4 >= text.length()) {
                            throw error("Incomplete \\u escape");
                        }
                        int code = 0;
                        for (int i = 1; i <= 4; i++) {
                            int digit = Character.digit(text.charAt(pos + i), 16);
                            if (digit < 0) {
                                throw error("Invalid \\u escape");
                            }
                            code = (code << 4) | digit;
                        }
                        sb.append((char) code);
                        pos += 4;
                        break;
                    default:
                        throw error("Invalid escape sequence '\\" + printable(e) + "'");
                }
                pos++;
                start = pos;
                continue;
            }
            pos++;
        }
    }

    private JsonNumber readNumber() {
        int start = pos;
        if (peek() == '-') {
            pos++;
        }
        if (peek() == '0') {
            pos++;
        } else if (isDigit(peek())) {
            while (isDigit(peek())) pos++;
        } else {
            throw error("Invalid number");
        }
        if (peek() == '.') {
            pos++;
            if (!isDigit(peek())) {
                throw error("Invalid number: digit expected after '.'");
            }
            while (isDigit(peek())) pos++;
        }
        if (peek() == 'e' || peek() == 'E') {
            pos++;
            if (peek() == '+' || peek() == '-') {
                pos++;
            }
            if (!isDigit(peek())) {
                throw error("Invalid number: digit expected in exponent");
            }
            while (isDigit(peek())) pos++;
        }
        return new JsonNumber(text.substring(start, pos));
    }

    private void expectLiteral(String literal) {
        if (!text.startsWith(literal, pos)) {
            throw error("Invalid literal");
        }
        pos += literal.length();
    }

    private void skipWhitespace() {
        while (pos < text.length()) {
            char c = text.charAt(pos);
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                pos++;
            } else {
                return;
            }
        }
    }

    private char peek() {
        return pos < text.length() ? text.charAt(pos) : '\0';
    }

    private static boolean isDigit(char c) {
        return c >= '0' && c <= '9';
    }

    private void checkDepth(int depth) {
        if (depth > MAX_DEPTH) {
            throw error("Maximum nesting depth of " + MAX_DEPTH + " exceeded");
        }
    }

    private JsonFormatException error(String message) {
        return new JsonFormatException("Input is not valid JSON: " + message + " at position " + pos + ".");
    }

    private static String printable(char c) {
        return c < 0x20 ? String.format(Locale.ROOT, "\\u%04x", (int) c) : String.valueOf(c);
    }
}
