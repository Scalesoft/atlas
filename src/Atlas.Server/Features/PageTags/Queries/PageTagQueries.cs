using Atlas.Server.Features.Search.Contracts;

namespace Atlas.Server.Features.PageTags.Queries;

// Reads live in a …Queries namespace, so their transactions commit without waiting for the WAL flush (TransactionIntent).
public sealed record GetPageTags(Guid PageId);
public sealed record SuggestTags(string? Query, bool AiScopeOnly = false);
public sealed record ListTaggedPages(Guid TagId, string? Query, Guid? SpaceId, int Offset, int Limit, SearchOptions Options, bool AiScopeOnly = false);
