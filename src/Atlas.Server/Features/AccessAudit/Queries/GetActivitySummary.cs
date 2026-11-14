namespace Atlas.Server.Features.AccessAudit.Queries;

/// <summary>Counts of editing, page opens and API/MCP/assistant calls over the last <c>Days</c> (1..3650), or since the beginning when null.</summary>
public sealed record GetActivitySummary(int? Days);
