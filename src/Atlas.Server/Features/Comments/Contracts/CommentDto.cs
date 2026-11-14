namespace Atlas.Server.Features.Comments.Contracts;

/// <summary>A comment as its reader sees it. A deleted one keeps its author and time with an empty body (the placeholder
/// „Komentář byl odstraněn“); CanEdit and CanDelete are what the reader may do with it now.</summary>
public sealed record CommentDto(Guid Id, Guid AuthorId, string AuthorName, string Body, string? Anchor, DateTime CreatedAt, DateTime? EditedAt,
    bool Deleted, bool CanEdit, bool CanDelete);

/// <summary>Newest first, at most <see cref="Queries.GetCommentsHandler.PageSize"/>; the next page is asked for before the last
/// item's id. Count is the object's live comments, CanPost whether the reader may write one now.</summary>
public sealed record CommentPage(IReadOnlyList<CommentDto> Items, bool HasMore, int Count, bool CanPost);

/// <summary>The object's live comments and the newest of them (null without one).</summary>
public sealed record CommentCount(int Count, Guid? LatestId);

public sealed record PostCommentRequest(string? Body, string? Anchor);

public sealed record EditCommentRequest(string? Body);

/// <summary>The live comment counts of the asked objects the reader may read and that have any (absent: none or not readable).</summary>
public sealed record CommentCounts(IReadOnlyDictionary<Guid, int> Counts);
