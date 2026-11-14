namespace Atlas.Server.Features.PersonalLibrary.Contracts;

public sealed record PersonalLibraryItem(Guid Id, Guid SpaceId, string SpaceName, string Title, Guid? ParentId,
    int Version, bool IsPublished, bool IsFavorite, DateTime? VisitedAt, DateTime? PublishedAt)
{
    /// <summary>The content type: <c>page</c>, or another type's resource among favorites and recent visits (<c>board</c>: no
    /// parent, Version 0, IsPublished true).</summary>
    public string Type { get; init; } = "page";
    public DateTime? ChangedAt { get; init; }
    public bool HasUnpublishedChanges { get; init; }
    /// <summary>Title of the visible parent page, so equally named pages tell apart ("Zákazník A › Přístupy").</summary>
    public string? ParentTitle { get; init; }
    /// <summary>Change summary of the current publication; filled for kind=updates only.</summary>
    public string? ChangeSummary { get; init; }
    /// <summary>Transport of the current publication (web, api, mcp, import, system; null from before 2.0); filled for kind=updates only.</summary>
    public string? PublicationChannel { get; init; }
    /// <summary>The token of an api or mcp publication, for its owner and wiki administrators only; kind=updates only.</summary>
    public ApiTokenRef? PublicationApiToken { get; init; }
}
