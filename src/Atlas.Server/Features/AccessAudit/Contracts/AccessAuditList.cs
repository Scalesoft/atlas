namespace Atlas.Server.Features.AccessAudit.Contracts;

public sealed record AccessAuditList(IReadOnlyList<AccessAuditItem> Items, bool HasMore, string? NextBeforeId, DateTime CoverageStartedAt);
