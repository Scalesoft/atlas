using System.Text.Json.Serialization;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PageDecorations;

namespace Atlas.Server.Features.Pages.Contracts;

public sealed record PageDto(Guid Id, Guid SpaceId, Guid? ParentId, string Title, int Version, bool IsPublished,
    string ContentJson, string Markdown, int? PublishedVersion, DateTime ActiveFrom, DateTime? ActiveTo, bool CanEdit)
{
    public PageDecorationDto? Decoration { get; init; }
    public bool IsTemplate { get; init; }

    /// <summary>A catalog page of its space's Projector (review C3-2); false with the module off.</summary>
    public bool IsCatalog { get; init; }

    /// <summary>3.6-I Živě: a reader reads this page's latest saved state and may follow it live, read-only
    /// (<c>GET /api/pages/{id}/collaboration</c>). Set for readers only; an editor reads the live state anyway. Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Live { get; init; }

    /// <summary>The page has its own access (not the space's): what is copied out of it to a space-wide place, such as a composed board,
    /// would reach every reader of the space. Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OwnAccess { get; init; }

    /// <summary>The personal API's <c>?format=markdown</c>: the Markdown with placeholders a save sends back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PageMarkdownSource? Source { get; init; }

    /// <summary>What a Markdown write of the personal API converted on the way (images, links, removed components).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>What a Markdown write did with its contacts (used, created, warnings), as MCP's write returns it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContactsReport? Contacts { get; init; }

    /// <summary>What a Markdown write linked (<c>link: "auto"</c>): every chip it made, the unresolved chips and the suggestions it only offers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Atlas.Server.Platform.Linking.LinkReport? Links { get; init; }
}
