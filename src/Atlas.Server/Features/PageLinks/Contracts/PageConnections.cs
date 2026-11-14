using System.Text.Json.Serialization;

namespace Atlas.Server.Features.PageLinks.Contracts;

/// <summary>The connections of a page shown below its content (§8.2 of the 2.0 design): pages linking here and, with the
/// <c>related</c> capability, related pages (R1).</summary>
public sealed record PageConnections(PageLinkList LinksHere)
{
    /// <summary>Absent from the JSON while the <c>related</c> capability is off (no AI, the task switched off, an embeddings
    /// outage: O4); links here and the status codes stay as they are. Present with an empty list when nothing is left after
    /// the reader's rights: the web hides the section either way.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RelatedPageList? Related { get; init; }
}

/// <summary>Up to the requested limit of the linking pages the reader may read; Total counts only those.</summary>
public sealed record PageLinkList(IReadOnlyList<LinkingPage> Items, int Total);

/// <summary>A linking page with its published title.</summary>
public sealed record LinkingPage(Guid Id, Guid SpaceId, string SpaceName, string Title);

/// <summary>At most five related pages the reader may read, most similar first. No count: a number would tell about pages the
/// reader may not see.</summary>
public sealed record RelatedPageList(IReadOnlyList<RelatedPage> Items)
{
    /// <summary>The contacts these pages name (PageLinkStore.RelatedPeopleAsync): the editor's people ranking, never shown.</summary>
    public IReadOnlyList<Guid> People { get; init; } = [];
}

/// <summary>A related page with its published title, shaped like <see cref="LinkingPage"/>.</summary>
public sealed record RelatedPage(Guid Id, Guid SpaceId, string SpaceName, string Title);
