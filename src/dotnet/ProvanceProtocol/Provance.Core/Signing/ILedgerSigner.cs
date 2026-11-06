namespace Provance.Core.Signing
{
    /// <summary>Outcome of a signature check.</summary>
    public enum SignatureCheck
    {
        /// <summary>The signature is valid for the given key.</summary>
        Valid,

        /// <summary>The key is known but the signature does not match.</summary>
        Invalid,

        /// <summary>The verifier does not know this key id.</summary>
        UnknownKey
    }

    /// <summary>
    /// Signs entry and checkpoint digests. The default implementation is <see cref="HmacLedgerSigner"/>.
    /// </summary>
    /// <remarks>
    /// Implement this interface to keep the key in a KMS or HSM (Azure Key Vault, AWS KMS, ...) so the
    /// application never holds it in clear. The writer calls <see cref="SignAsync"/> once per entry:
    /// a remote signer adds its latency to every append.
    /// </remarks>
    public interface ILedgerSigner
    {
        /// <summary>Identifier of the key used by <see cref="SignAsync"/>. Stored in every entry.</summary>
        string KeyId { get; }

        /// <summary>Signs a 32 byte SHA-256 digest with the current key.</summary>
        ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken = default);

        /// <summary>Checks a signature produced by the key <paramref name="keyId"/>, current or retired.</summary>
        ValueTask<SignatureCheck> VerifyAsync(string keyId, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default);
    }
}
