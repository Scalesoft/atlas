namespace Atlas.Server.Features.ContentImport.Contracts;

/// <summary>
/// One construct of a source page that did not arrive unchanged. <paramref name="Impact"/> is
/// <see cref="Degraded"/> (content kept in a different form), <see cref="Lost"/> (only the original ZIP
/// keeps it) or <see cref="Unresolved"/> (a link whose target is not part of the import). Occurrences of
/// the same construct are aggregated; <paramref name="Line"/> and <paramref name="Excerpt"/> describe the first.
/// </summary>
public sealed record WikiImportFinding(string Construct, string Impact, int Count, int? Line, string? Excerpt, string Message)
{
    public const string Degraded = "degraded";
    public const string Lost = "lost";
    public const string Unresolved = "unresolved";
}
