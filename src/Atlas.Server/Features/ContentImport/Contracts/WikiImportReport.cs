namespace Atlas.Server.Features.ContentImport.Contracts;

public sealed record WikiImportReport(Guid Id, string ReviewToken, DateTime ExpiresAt, Guid? SpaceId, string SpaceName,
    WikiImportCounts Counts, IReadOnlyList<WikiImportItem> Items, IReadOnlyList<string> Warnings, bool CanCommit, DateTime? CompletedAt = null);
