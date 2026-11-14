namespace Atlas.Server.Features.Attachments.Queries;

public sealed record OpenAttachment(Guid Id, bool Inline, Guid? AuthenticatedTokenId = null);
