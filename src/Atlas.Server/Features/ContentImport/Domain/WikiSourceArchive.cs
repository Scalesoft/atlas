namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// The uploaded export. <paramref name="Skipped"/> holds files that could have become a page but
/// deliberately did not, keyed by archive path with the reason shown in the report; the file itself
/// always stays in the archive.
/// </summary>
public sealed record WikiSourceArchive(
    IReadOnlyDictionary<string, WikiSourceFile> Files,
    IReadOnlyList<WikiSourcePage> Pages,
    IReadOnlyDictionary<string, string> Skipped)
{
    /// <summary>Entries left out of <see cref="Files"/> (unsafe path, symbolic link), keyed by a printable form of their name, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Excluded { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Exact spelling (ordinal) of an entry renamed because another entry differs from it only in letter
    /// case, to its unique path. Page files are also keyed without extension and with <c>.md</c>, so a link
    /// spelled exactly like the renamed page still reaches it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Renamed { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The leading folder removed from every path (<see cref="Contracts.WikiImportOptions.StripPrefix"/>), for the report.</summary>
    public string? StrippedPrefix { get; init; }
}
