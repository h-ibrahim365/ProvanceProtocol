using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Provance.Core.Cryptography;

namespace Provance.Core.Data
{
    /// <summary>
    /// A signed statement "the ledger head was at <see cref="Sequence"/> with <see cref="Hash"/>".
    /// </summary>
    /// <remarks>
    /// Checkpoints are what makes truncation and full rewrites detectable: publish them somewhere the
    /// application cannot modify (object storage with retention lock, WORM storage, a timestamping
    /// authority, another team's system) and give the latest one to the verifier.
    /// </remarks>
    public sealed record LedgerCheckpoint
    {
        /// <summary>Format version of the checkpoint.</summary>
        [JsonPropertyName("v")]
        public required int FormatVersion { get; init; }

        /// <summary>Ledger the checkpoint refers to.</summary>
        public required string LedgerId { get; init; }

        /// <summary>Sequence of the head entry at checkpoint time.</summary>
        public required long Sequence { get; init; }

        /// <summary>Hash of the head entry at checkpoint time.</summary>
        public required string Hash { get; init; }

        /// <summary>UTC creation time, truncated to the millisecond. Written as <c>2026-11-07T09:00:06.789Z</c>, like the other libraries.</summary>
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>Key used to sign the checkpoint.</summary>
        public required string KeyId { get; init; }

        /// <summary>Lowercase hex signature of the SHA-256 of the canonical checkpoint body.</summary>
        public required string Signature { get; init; }

        /// <summary>Serializes the checkpoint to JSON (camelCase).</summary>
        public string ToJson() => JsonSerializer.Serialize(this, ProvanceProtocol.WebSerializerOptions);

        /// <summary>Parses a checkpoint written by any PROVANCE library (offset <c>Z</c> or <c>+00:00</c>).</summary>
        public static LedgerCheckpoint FromJson(string json)
            => JsonSerializer.Deserialize<LedgerCheckpoint>(json, ProvanceProtocol.WebSerializerOptions)
               ?? throw new FormatException("Checkpoint JSON is empty.");

        /// <summary>Writes the canonical timestamp format; reads any ISO 8601 date and time.</summary>
        private sealed class UtcMillisecondsConverter : JsonConverter<DateTimeOffset>
        {
            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("createdAt is null."),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
                => writer.WriteStringValue(LedgerHashing.FormatTimestamp(value));
        }
    }
}
