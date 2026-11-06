using Provance.Core.Canonicalization;

namespace Provance.Core.Tests
{
    public class CanonicalizationTests
    {
        [Fact]
        public void Rfc8785_section_3_2_2_example()
        {
            const string input = """
                {
                  "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
                  "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
                  "literals": [null, true, false]
                }
                """;
            const string expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";

            Assert.Equal(expected, JsonCanonicalizer.Canonicalize(input));
        }

        [Fact]
        public void Members_are_sorted_by_utf16_code_units()
        {
            const string input = "{\"\\u20ac\":1,\"\\r\":2,\"\\ufb33\":3,\"1\":4,\"\\ud83d\\ude00\":5,\"\\u0080\":6,\"\\u00f6\":7}";
            string canonical = JsonCanonicalizer.Canonicalize(input);
            Assert.Equal("{\"\\r\":2,\"1\":4,\"\u0080\":6,\"ö\":7,\"€\":1,\"\ud83d\ude00\":5,\"\ufb33\":3}", canonical);
        }

        [Theory]
        [InlineData("0", "0")]
        [InlineData("-0", "0")]
        [InlineData("1e21", "1e+21")]
        [InlineData("1e20", "100000000000000000000")]
        [InlineData("0.000001", "0.000001")]
        [InlineData("1e-7", "1e-7")]
        [InlineData("123.456", "123.456")]
        [InlineData("-1.5e-7", "-1.5e-7")]
        [InlineData("5e-324", "5e-324")]
        [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
        [InlineData("9007199254740992", "9007199254740992")]
        [InlineData("295147905179352825856", "295147905179352830000")]
        [InlineData("1e23", "1e+23")]
        [InlineData("4.50", "4.5")]
        public void Numbers_follow_the_ecmascript_format(string literal, string expected)
        {
            Assert.Equal(expected, JsonCanonicalizer.FormatNumber(literal));
        }

        [Fact]
        public void Whitespace_and_escaping_are_normalized()
        {
            Assert.Equal("{\"a\":\"é<>&\",\"b\":[1,2]}", JsonCanonicalizer.Canonicalize("{ \"b\" : [ 1 , 2 ] , \"a\" : \"\\u00e9\\u003c>&\" }"));
        }

        [Fact]
        public void Duplicate_member_names_are_rejected()
        {
            Assert.Throws<FormatException>(() => JsonCanonicalizer.Canonicalize("{\"a\":1,\"a\":2}"));
        }

        [Fact]
        public void Invalid_json_is_rejected()
        {
            Assert.Throws<FormatException>(() => JsonCanonicalizer.Canonicalize("{\"a\":1,}"));
            Assert.Throws<FormatException>(() => JsonCanonicalizer.Canonicalize("{\"a\":1e400}"));
        }

        [Fact]
        public void Serialize_uses_the_runtime_type()
        {
            BasePayload payload = new DerivedPayload { Name = "n", Secret = "s" };
            string json = JsonCanonicalizer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.Equal("{\"name\":\"n\",\"secret\":\"s\"}", json);
        }

        private record BasePayload
        {
            public string? Name { get; init; }
        }

        private sealed record DerivedPayload : BasePayload
        {
            public string? Secret { get; init; }
        }
    }
}
