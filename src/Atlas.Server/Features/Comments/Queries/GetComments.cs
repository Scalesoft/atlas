namespace Atlas.Server.Features.Comments.Queries;

/// <param name="Before">The id of the oldest comment the reader has; null for the newest page.</param>
public sealed record GetComments(Guid ObjectId, Guid? Before);
