package io.github.hibrahim365.provance.json;

import org.junit.jupiter.api.Test;

import java.math.BigDecimal;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

class CanonicalizationTests {

    @Test
    void rfc8785Section322Example() {
        String input = "{\n  \"numbers\": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],\n"
                + "  \"string\": \"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"\\/\",\n"
                + "  \"literals\": [null, true, false]\n}";
        String expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],"
                + "\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";
        assertEquals(expected, JsonCanonicalizer.canonicalize(input));
    }

    @Test
    void membersAreSortedByUtf16CodeUnits() {
        // RFC 8785 section 3.2.3 example: sorting by code units, not code points.
        String input = "{\"\\u20ac\":\"Euro\",\"\\r\":\"CR\",\"\\ufb33\":\"Hebrew\",\"1\":\"One\",\"\\ud83d\\ude00\":\"Smiley\",\"\\u0080\":\"Control\",\"\\u00f6\":\"o\"}";
        String expected = "{\"\\r\":\"CR\",\"1\":\"One\",\"\u0080\":\"Control\",\"ö\":\"o\",\"€\":\"Euro\",\"\ud83d\ude00\":\"Smiley\",\"\ufb33\":\"Hebrew\"}";
        assertEquals(expected, JsonCanonicalizer.canonicalize(input));
    }

    @Test
    void numbersFollowTheEcmascriptFormat() {
        String[][] cases = {
                {"0", "0"}, {"-0", "0"}, {"1e21", "1e+21"}, {"1e20", "100000000000000000000"}, {"0.000001", "0.000001"},
                {"1e-7", "1e-7"}, {"123.456", "123.456"}, {"-1.5e-7", "-1.5e-7"}, {"5e-324", "5e-324"},
                {"1.7976931348623157e308", "1.7976931348623157e+308"}, {"9007199254740992", "9007199254740992"},
                {"295147905179352825856", "295147905179352830000"}, {"1e23", "1e+23"}, {"4.50", "4.5"},
                {"0.1", "0.1"}, {"5e-7", "5e-7"}, {"2e-3", "0.002"}
        };
        for (String[] c : cases) {
            assertEquals(c[1], JsonCanonicalizer.formatNumber(c[0]), c[0]);
        }
    }

    @Test
    void doublesUseTheShortestRoundTrippingDigits() {
        assertEquals("0.30000000000000004", JsonCanonicalizer.formatDouble(0.1 + 0.2));
        assertEquals("0.3333333333333333", JsonCanonicalizer.formatDouble(1.0 / 3));
        assertEquals("2.2250738585072014e-308", JsonCanonicalizer.formatDouble(Double.MIN_NORMAL));
        assertEquals("-1e-7", JsonCanonicalizer.formatDouble(-1e-7));
    }

    @Test
    void whitespaceAndEscapingAreNormalized() {
        assertEquals("{\"a\":\"\\u001f\\t\\\"\",\"b\":[1,2]}", JsonCanonicalizer.canonicalize(" { \"b\" : [ 1 , 2.0 ] , \"a\" : \"\\u001F\\t\\\"\" } "));
    }

    @Test
    void invalidInputIsRejected() {
        String[] invalid = {"{\"a\":1,\"a\":2}", "{\"a\":1,}", "[1 2]", "{'a':1}", "01", "1.", "-", "\"\\ud800\"", "1e999",
                "{\"a\":1} x", "\"tab\tinside\"", "nul", "[" + "[".repeat(70) + "]".repeat(71)};
        for (String json : invalid) {
            assertThrows(JsonFormatException.class, () -> JsonCanonicalizer.canonicalize(json), json);
        }
    }

    public record Order(String orderId, String amount, Map<String, Object> tags, String note) {
    }

    public static final class Bean {
        public String getName() {
            return "bean";
        }

        public boolean isActive() {
            return true;
        }

        public String getMissing() {
            return null;
        }
    }

    enum Status { OPEN }

    @Test
    void simpleSerializerHandlesRecordsBeansMapsAndValueTypes() {
        Map<String, Object> tags = new LinkedHashMap<>();
        tags.put("z", null);
        tags.put("a", List.of(1, 2L, 1.5, new BigDecimal("10.00"), Status.OPEN));
        String json = JsonCanonicalizer.serialize(new Order("A-1", "49.90", tags, null), SimplePayloadSerializer.INSTANCE);
        assertEquals("{\"amount\":\"49.90\",\"orderId\":\"A-1\",\"tags\":{\"a\":[1,2,1.5,10,\"OPEN\"],\"z\":null}}", json);

        assertEquals("{\"active\":true,\"name\":\"bean\"}", JsonCanonicalizer.serialize(new Bean(), SimplePayloadSerializer.INSTANCE));

        UUID id = UUID.fromString("6f1c2a5e-3b4d-4e8f-9a0b-1c2d3e4f5a6b");
        Instant at = Instant.parse("2026-01-15T09:30:00.123Z");
        assertEquals("[\"6f1c2a5e-3b4d-4e8f-9a0b-1c2d3e4f5a6b\",\"2026-01-15T09:30:00.123Z\",\"AQI=\"]",
                JsonCanonicalizer.serialize(new Object[] {id, at, new byte[] {1, 2}}, SimplePayloadSerializer.INSTANCE));
    }

    @Test
    void auditedPayloadSerializesLikeTheDotNetRecord() {
        String json = JsonCanonicalizer.serialize(
                io.github.hibrahim365.provance.AuditedPayload.of("user-42", "Refund issued", Map.of("orderId", "A-1001")),
                SimplePayloadSerializer.INSTANCE);
        assertEquals("{\"actorId\":\"user-42\",\"data\":{\"orderId\":\"A-1001\"},\"description\":\"Refund issued\"}", json);
    }

    @Test
    void jsonParseReturnsPlainJavaValues() {
        Object value = Json.parse("{\"n\":1,\"d\":1.5,\"big\":12345678901234567890,\"s\":\"x\",\"l\":[true,null]}");
        Map<?, ?> map = (Map<?, ?>) value;
        assertEquals(1L, map.get("n"));
        assertEquals(1.5, map.get("d"));
        assertEquals(1.2345678901234567e19, map.get("big"));
        assertEquals(java.util.Arrays.asList(true, null), map.get("l"));
    }
}
