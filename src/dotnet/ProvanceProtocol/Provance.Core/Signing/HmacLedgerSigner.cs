using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Provance.Core.Options;

namespace Provance.Core.Signing
{
    /// <summary>
    /// HMAC-SHA256 signer using the keys configured in <see cref="ProvanceOptions"/>.
    /// </summary>
    /// <remarks>
    /// HMAC is symmetric: whoever can verify can also sign. Combine it with external checkpoints, or
    /// replace it with an <see cref="ILedgerSigner"/> backed by a KMS, if the people who run the
    /// application must not be able to rewrite history.
    /// </remarks>
    public sealed class HmacLedgerSigner : ILedgerSigner
    {
        /// <summary>Minimum key length, in UTF-8 bytes.</summary>
        public const int MinimumKeyBytes = 32;

        private static readonly string[] KnownPlaceholders =
        [
            "DEFAULT_INSECURE_KEY_CHANGE_ME",
            "CHANGE_ME",
            "CHANGE_ME_IN_PROD",
            "MY_SUPER_SECRET_LOCAL_DEV_KEY_12345"
        ];

        private readonly byte[] _signingKey;
        private readonly Dictionary<string, byte[]> _keys;

        /// <summary>Creates a signer from the options (<see cref="ProvanceOptions.SigningKeyId"/>, <see cref="ProvanceOptions.SecretKey"/> and <see cref="ProvanceOptions.RetiredKeys"/>).</summary>
        public HmacLedgerSigner(IOptions<ProvanceOptions> options)
            : this(options.Value.SigningKeyId, options.Value.SecretKey, options.Value.RetiredKeys)
        {
        }

        /// <summary>Creates a signer with an explicit current key and optional retired keys (verification only).</summary>
        /// <exception cref="InvalidOperationException">A key is missing, too short or a known placeholder.</exception>
        public HmacLedgerSigner(string keyId, string? secretKey, IReadOnlyDictionary<string, string>? retiredKeys = null)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                throw new InvalidOperationException("PROVANCE: SigningKeyId is required.");
            }

            KeyId = keyId;
            _signingKey = ValidateKey(keyId, secretKey);
            _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [keyId] = _signingKey };

            if (retiredKeys is not null)
            {
                foreach (var (id, secret) in retiredKeys)
                {
                    if (string.Equals(id, keyId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"PROVANCE: key id '{id}' is both the signing key and a retired key.");
                    }
                    _keys[id] = ValidateKey(id, secret);
                }
            }
        }

        /// <inheritdoc />
        public string KeyId { get; }

        /// <inheritdoc />
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(HMACSHA256.HashData(_signingKey, digest.Span));

        /// <inheritdoc />
        public ValueTask<SignatureCheck> VerifyAsync(string keyId, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default)
        {
            if (!_keys.TryGetValue(keyId, out var key))
            {
                return ValueTask.FromResult(SignatureCheck.UnknownKey);
            }

            byte[] expected = HMACSHA256.HashData(key, digest.Span);
            return ValueTask.FromResult(CryptographicOperations.FixedTimeEquals(expected, signature.Span)
                ? SignatureCheck.Valid
                : SignatureCheck.Invalid);
        }

        private static byte[] ValidateKey(string keyId, string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"PROVANCE: no secret configured for key '{keyId}'. Set Provance:SecretKey from a secret store, or register your own ILedgerSigner.");
            }

            if (KnownPlaceholders.Any(p => string.Equals(p, secret, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"PROVANCE: the secret of key '{keyId}' is a placeholder value. Generate a real key.");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(secret);
            if (bytes.Length < MinimumKeyBytes)
            {
                throw new InvalidOperationException(
                    $"PROVANCE: the secret of key '{keyId}' is {bytes.Length} bytes long, the minimum is {MinimumKeyBytes}.");
            }
            return bytes;
        }
    }
}
