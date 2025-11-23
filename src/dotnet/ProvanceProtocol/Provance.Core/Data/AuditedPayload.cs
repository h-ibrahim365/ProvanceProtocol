namespace Provance.Core.Data
{
    /// <summary>
    /// Contains the specific data for the event being audited.
    /// This is the business content that will be included in the hash.
    /// </summary>
    public class AuditedPayload
    {
        /// <summary>
        /// Identifier of the user or system that performed the action.
        /// </summary>
        public string? ActorId { get; set; }

        /// <summary>
        /// Name or role of the actor (for log readability).
        /// </summary>
        public string? ActorRole { get; set; }

        /// <summary>
        /// Textual description or metadata of the action.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Dictionary to store free-form, application-specific data fields.
        /// </summary>
        public Dictionary<string, object>? CustomData { get; set; }
    }
}