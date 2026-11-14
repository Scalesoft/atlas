namespace Atlas.Server.Features.Comments.Commands;

public sealed record PostComment(Guid ObjectId, string? Body, string? Anchor);
