namespace Provance.Core.Data
{
    /// <summary>
    /// A general purpose payload shape. Any serializable object can be used as a payload;
    /// this type is only a convenient default.
    /// </summary>
    /// <remarks>
    /// The payload is serialized with its runtime type, so properties of derived types are signed too.
    /// Keep personal data out of payloads when you can: the ledger is append only.
    /// </remarks>
    public record AuditedPayload
    {
        /// <summary>Identifier of the user or system that performed the action.</summary>
        public string? ActorId { get; init; }

        /// <summary>Role or kind of actor, for readability.</summary>
        public string? ActorRole { get; init; }

        /// <summary>Short description of the action.</summary>
        public string? Description { get; init; }

        /// <summary>Free form, application specific data.</summary>
        /// <remarks>Store money amounts, large integers and precise decimals as strings (see RFC 8785 number rules).</remarks>
        public IReadOnlyDictionary<string, object?>? Data { get; init; }
    }
}
