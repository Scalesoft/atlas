using Atlas.Server.Features.AccessAudit.Contracts;

namespace Atlas.Server.Features.Vaults.Audit;

/// <summary>One page of a vault or secret audit, in the shape of the global access audit; NextBeforeId continues it.</summary>
public sealed record VaultAuditList(IReadOnlyList<AccessAuditItem> Items, bool HasMore, string? NextBeforeId);
