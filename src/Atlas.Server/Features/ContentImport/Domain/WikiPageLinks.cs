namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// The link callbacks the converter uses for one page: <paramref name="Url"/> resolves a Markdown
/// destination (image flag), <paramref name="Wiki"/> a <c>[[target|alias]]</c> link (embed flag) and
/// <paramref name="Self"/> links the page itself, optionally to one of its headings (null while the
/// page has no address). <see cref="ImportLinkResolver"/> supplies the import-wide implementation.
/// </summary>
public sealed record WikiPageLinks(
    Func<string, bool, LinkResolution> Url,
    Func<string, string?, bool, LinkResolution> Wiki,
    Func<string?, string?> Self)
{
    /// <summary>Resolves nothing; used to read a page's heading outline before links are known.</summary>
    public static WikiPageLinks Offline { get; } = new((_, _) => LinkResolution.None, (_, _, _) => LinkResolution.None, _ => null);
}
