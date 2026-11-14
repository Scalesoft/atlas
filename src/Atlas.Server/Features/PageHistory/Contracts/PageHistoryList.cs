namespace Atlas.Server.Features.PageHistory.Contracts;

public sealed record PageHistoryList(Guid PageId, int CurrentVersion, IReadOnlyList<PageHistoryItem> Items, int? NextBeforeVersion);
