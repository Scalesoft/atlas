using Atlas.Server.Features.PageDecorations;

namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>
/// The web client's page: <see cref="PageDto"/> without the Markdown projection, which the reader and the
/// editor never use (they render ContentJson) and which roughly doubles every page body. The personal API
/// keeps answering with <see cref="PageDto"/>.
/// </summary>
public sealed record PageView(Guid Id, Guid SpaceId, Guid? ParentId, string Title, int Version, bool IsPublished,
    string ContentJson, int? PublishedVersion, DateTime ActiveFrom, DateTime? ActiveTo, bool CanEdit, PageDecorationDto? Decoration,
    bool IsTemplate)
{
    public bool IsCatalog { get; init; }

    /// <summary><see cref="PageDto.OwnAccess"/>, written only when true.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool OwnAccess { get; init; }

    /// <summary><see cref="PageDto.Live"/>, written only when true.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Live { get; init; }

    /// <summary>The related reads, only with <c>?bundle=1</c> (<see cref="PageBundle"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PageBundle? Bundle { get; init; }

    public static PageView From(PageDto page) => new(page.Id, page.SpaceId, page.ParentId, page.Title, page.Version, page.IsPublished,
        page.ContentJson, page.PublishedVersion, page.ActiveFrom, page.ActiveTo, page.CanEdit, page.Decoration, page.IsTemplate)
    {
        IsCatalog = page.IsCatalog,
        OwnAccess = page.OwnAccess,
        Live = page.Live
    };
}
