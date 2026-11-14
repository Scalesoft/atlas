using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// How one link of a page was resolved. <paramref name="Href"/> is null when the link stays text;
/// <paramref name="Finding"/> names a construct to report (with <paramref name="Impact"/>) even when
/// the link resolved, e.g. a heading anchor that fell back to the page. <paramref name="Label"/> is the
/// display text chosen for wiki links, whose alias order differs between source wikis.
/// </summary>
public sealed record LinkResolution(string? Href, string? Finding = null, string Impact = WikiImportFinding.Unresolved, string? Label = null)
{
    public static LinkResolution None { get; } = new(Href: null);
}
