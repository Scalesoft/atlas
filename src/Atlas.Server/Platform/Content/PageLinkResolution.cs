namespace Atlas.Server.Platform.Content;

/// <summary>How a page title resolved for <c>[[Název]]</c>: exactly one readable page, several, or none.</summary>
public enum PageLinkMatch
{
    Unique,
    Ambiguous,
    None,
}

/// <summary>
/// The answer of <see cref="MergeContext.ResolveTitle"/>. Only <see cref="PageLinkMatch.Unique"/> carries the page; "none"
/// covers pages that do not exist and pages the actor may not read alike.
/// </summary>
public sealed record PageLinkResolution(PageLinkMatch Match, Guid PageId, string Title)
{
    public static PageLinkResolution Unique(Guid pageId, string title) => new(PageLinkMatch.Unique, pageId, title);

    public static PageLinkResolution Ambiguous { get; } = new(PageLinkMatch.Ambiguous, Guid.Empty, "");

    public static PageLinkResolution None { get; } = new(PageLinkMatch.None, Guid.Empty, "");
}
