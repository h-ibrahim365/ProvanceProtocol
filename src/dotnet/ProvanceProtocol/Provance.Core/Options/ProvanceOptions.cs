namespace Provance.Core.Options
{
    /// <summary>
    /// Configuration options for the PROVANCE protocol.
    /// </summary>
    public class ProvanceOptions
    {
        /// <summary>
        /// The immutable starting hash of the chain (block 0).
        /// This must be a valid SHA-256 string and is CRITICAL for security.
        /// The 'required' keyword ensures this property must be set upon initialization.
        /// </summary>
        public required string GenesisHash { get; set; }

        /// <summary>
        /// The security level for hashing.
        /// </summary>
        public string HashAlgorithm { get; set; } = "SHA256";
    }
}