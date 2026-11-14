namespace Atlas.Server.Features.Comments.Commands;

public sealed record DeleteComment(Guid ObjectId, Guid CommentId);
