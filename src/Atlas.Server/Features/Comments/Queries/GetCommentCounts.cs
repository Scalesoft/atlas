namespace Atlas.Server.Features.Comments.Queries;

/// <param name="ObjectIds">At most <see cref="GetCommentCountsHandler.MaxObjects"/>.</param>
public sealed record GetCommentCounts(IReadOnlyList<Guid> ObjectIds);
