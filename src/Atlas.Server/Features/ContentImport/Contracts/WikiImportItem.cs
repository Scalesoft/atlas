namespace Atlas.Server.Features.ContentImport.Contracts;

/// <summary>
/// <paramref name="Warnings"/> are decisions about the item as a whole (conflicts, preserved originals);
/// <paramref name="Findings"/> is the per-construct conversion report. Reports stored before findings
/// existed deserialise with <c>null</c> for both optional lists.
/// </summary>
public sealed record WikiImportItem(string Path, string Kind, string Action, string? Title, Guid? PageId, int? ExpectedVersion, string Sha256, IReadOnlyList<string> Warnings,
    IReadOnlyList<WikiImportFinding>? Findings = null, IReadOnlyList<string>? Tags = null);
