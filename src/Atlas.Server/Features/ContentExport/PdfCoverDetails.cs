namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// What a PDF states about the exported revision on its cover: the revision number (also in the page header), whether it is
/// published, its date, the export date and the page id. A formal document may leave out some or all of them; without a
/// choice the PDF states all, as it always has. Names in this canonical order, stored comma-separated.
/// </summary>
public static class PdfCoverDetails
{
    public const string Revision = "revision";
    public static readonly IReadOnlyList<string> All = [Revision, "state", "revisionDate", "exportedAt", "pageId"];
    public static readonly string Default = string.Join(',', All);

    /// <summary>The chosen details in canonical order; null means all, an empty choice none. Refuses an unknown name (400).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? details)
    {
        if (details is null) return All;
        var chosen = details.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (chosen.FirstOrDefault(x => !All.Contains(x)) is { } unknown)
            throw new ApiException(400, "Neznámý údaj titulní strany PDF „{unknown}“. Povolené jsou: {default}.", args: new Dictionary<string, object?> { ["unknown"] = unknown, ["default"] = Default });
        return All.Where(chosen.Contains).ToList();
    }

    /// <summary>A comma-separated choice (query string or stored value); null means all.</summary>
    public static IReadOnlyList<string> Parse(string? details) => Normalize(details?.Split(','));

    public static string Join(IEnumerable<string>? details) => string.Join(',', Normalize(details));
}
