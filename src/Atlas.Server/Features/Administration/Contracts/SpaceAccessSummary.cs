namespace Atlas.Server.Features.Administration.Contracts;

/// <summary>The current grants of a space. People and groups for a new grant come from GET /api/access/candidates, so this no longer
/// lists the account directory (access audit 2026-09-28).</summary>
public sealed record SpaceAccessSummary(IReadOnlyList<GrantSummary> Grants);
