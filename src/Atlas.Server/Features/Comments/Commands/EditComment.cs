namespace Atlas.Server.Features.Comments.Commands;

public sealed record EditComment(Guid ObjectId, Guid CommentId, string? Body);
