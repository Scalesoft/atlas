namespace Atlas.Server.Features.ApiActivity.Commands;

/// <summary>
/// One write attempt over the personal API (channel api), MCP (channel mcp) or the in-process assistant (channel assistant, the only
/// one without a token): who, which token, the operation (a REST operation from <see cref="ApiOperations"/> or an MCP tool name), the
/// target ids, the outcome code and the resulting page version. Never content, a title or an error text. Sent in its own transaction
/// after the result, so a refusal whose handler rolled back is recorded too. The event in the access audit is api.write;
/// mcp.publish or assistant.publish for the tool atlas_publish; mcp.write or assistant.write for every other tool.
/// Callers normally go through <see cref="ApiActivityAudit"/>, which reads actor, token and channel from the request.
/// </summary>
public sealed record RecordApiActivity(Guid ActorId, Guid? TokenId, string Channel, string Operation, Guid? PageId, Guid? SpaceId,
    string Outcome, int? ResultVersion, string? RequestId);
