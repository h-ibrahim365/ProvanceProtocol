using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Provance.Core.Cryptography;

namespace Provance.Core.Options
{
    /// <summary>What <c>AddEntryAsync</c> does when the in-memory queue is full.</summary>
    public enum QueueFullBehavior
    {
        /// <summary>Wait asynchronously until there is room (backpressure).</summary>
        Wait,

        /// <summary>Throw <see cref="Exceptions.LedgerOverloadedException"/> immediately.</summary>
        FailFast
    }

    /// <summary>
    /// Configuration of a PROVANCE ledger. Bound from the <c>Provance</c> configuration section by default.
    /// </summary>
    public sealed class ProvanceOptions
    {
        /// <summary>Default configuration section name.</summary>
        public const string SectionName = "Provance";

        /// <summary>
        /// Required. Stable identifier of the ledger, for example <c>orders-api-prod</c>.
        /// Allowed characters: letters, digits, <c>. _ : -</c>, up to 128 characters.
        /// </summary>
        public string LedgerId { get; set; } = string.Empty;

        /// <summary>
        /// Optional. Hash used as <c>PreviousHash</c> of the first entry (64 lowercase hex characters).
        /// Defaults to <see cref="LedgerHashing.DeriveGenesisHash"/> of <see cref="LedgerId"/>.
        /// </summary>
        public string? GenesisHash { get; set; }

        /// <summary>Identifier of the current signing key. Change it whenever you rotate the key.</summary>
        public string SigningKeyId { get; set; } = "k1";

        /// <summary>
        /// HMAC secret of the current key, at least 32 bytes. Load it from a secret store, never from appsettings.json.
        /// Not used when you register your own <see cref="Signing.ILedgerSigner"/>.
        /// </summary>
        public string? SecretKey { get; set; }

        /// <summary>Retired keys (key id to secret), used only to verify old entries after a rotation.</summary>
        public Dictionary<string, string> RetiredKeys { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Maximum number of entries waiting for the writer. Default 10 000.</summary>
        public int QueueCapacity { get; set; } = 10_000;

        /// <summary>Behavior when the queue is full. Default <see cref="QueueFullBehavior.Wait"/>.</summary>
        public QueueFullBehavior QueueFullBehavior { get; set; } = QueueFullBehavior.Wait;

        /// <summary>Maximum number of entries appended to the store in one call. Default 256.</summary>
        public int MaxBatchSize { get; set; } = 256;

        /// <summary>Attempts for a batch that fails with a transient store error before callers get an exception. Default 5.</summary>
        public int MaxAppendAttempts { get; set; } = 5;

        /// <summary>Base delay of the exponential backoff between transient failures. Default 50 ms, capped at 2 s.</summary>
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Attempts when another writer appended first (sequence conflict). Conflicts are resolved by
        /// reloading the head and resealing, without delay. Default 100.
        /// </summary>
        public int MaxConflictRetries { get; set; } = 100;

        /// <summary>Interval between automatic checkpoints. Only used when a checkpoint sink is registered. Default 5 minutes.</summary>
        public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Serializer options used to turn payload objects into JSON before canonicalization.
        /// Default: web defaults (camelCase), null properties omitted.
        /// </summary>
        public JsonSerializerOptions PayloadSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>The genesis hash actually used: <see cref="GenesisHash"/> if set, otherwise derived from <see cref="LedgerId"/>.</summary>
        public string EffectiveGenesisHash
            => string.IsNullOrEmpty(GenesisHash) ? LedgerHashing.DeriveGenesisHash(LedgerId) : GenesisHash;
    }

    /// <summary>Validates <see cref="ProvanceOptions"/> at startup.</summary>
    internal sealed partial class ProvanceOptionsValidator : IValidateOptions<ProvanceOptions>
    {
        [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
        private static partial Regex LedgerIdRegex();

        public ValidateOptionsResult Validate(string? name, ProvanceOptions options)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(options.LedgerId))
                errors.Add("Provance:LedgerId is required (for example \"orders-api-prod\").");
            else if (!LedgerIdRegex().IsMatch(options.LedgerId))
                errors.Add("Provance:LedgerId may only contain letters, digits and . _ : - (max 128 characters).");

            if (!string.IsNullOrEmpty(options.GenesisHash) && !LedgerHashing.IsLowerHex(options.GenesisHash, 64))
                errors.Add("Provance:GenesisHash must be 64 lowercase hex characters.");

            if (string.IsNullOrWhiteSpace(options.SigningKeyId))
                errors.Add("Provance:SigningKeyId is required.");

            if (options.QueueCapacity < 1)
                errors.Add("Provance:QueueCapacity must be at least 1.");

            if (options.MaxBatchSize is < 1 or > 10_000)
                errors.Add("Provance:MaxBatchSize must be between 1 and 10000.");

            if (options.MaxAppendAttempts < 1)
                errors.Add("Provance:MaxAppendAttempts must be at least 1.");

            if (options.MaxConflictRetries < 0)
                errors.Add("Provance:MaxConflictRetries cannot be negative.");

            if (options.RetryBaseDelay < TimeSpan.Zero)
                errors.Add("Provance:RetryBaseDelay cannot be negative.");

            if (options.CheckpointInterval <= TimeSpan.Zero)
                errors.Add("Provance:CheckpointInterval must be positive.");

            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
