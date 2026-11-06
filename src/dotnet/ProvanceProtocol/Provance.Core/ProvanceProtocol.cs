using System.Text.Json;

namespace Provance.Core
{
    /// <summary>
    /// Constants of the PROVANCE ledger format.
    /// </summary>
    public static class ProvanceProtocol
    {
        /// <summary>Version of the signed envelope written by this library (field <c>v</c>).</summary>
        public const int FormatVersion = 1;

        /// <summary>Largest sequence number allowed. Sequences are JSON numbers, so they must stay exact as IEEE 754 doubles.</summary>
        public const long MaxSequence = (1L << 53) - 1;

        /// <summary>Prefix hashed with the ledger id to derive the default genesis hash.</summary>
        public const string GenesisPrefix = "provance:genesis:v1:";

        internal static readonly JsonSerializerOptions WebSerializerOptions = new(JsonSerializerDefaults.Web);
    }
}
