namespace Provance.AspNetCore.Middleware.Dtos
{
    /// <summary>
    /// Data Transfer Object (DTO) representing the structured data sent by the client
    /// to initiate a new Ledger entry.
    /// </summary>
    public record LedgerRequest(
        string EventType,
        string ActorId, // Often included in the request body for traceability (though often overridden by claims)
        string Description,
        Dictionary<string, object>? CustomData
    );
}