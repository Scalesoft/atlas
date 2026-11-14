namespace Atlas.Server.Features.Pages.Contracts;

public sealed record SpaceDraftItem(Guid Id, Guid SpaceId, string Title, int Version, Guid RevisionId,
    Guid? PublishedRevisionId, int? PublishedVersion, Guid AuthorId, string AuthorName, DateTime ChangedAt);
