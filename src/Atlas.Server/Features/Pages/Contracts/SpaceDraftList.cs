namespace Atlas.Server.Features.Pages.Contracts;

public sealed record SpaceDraftList(IReadOnlyList<SpaceDraftItem> Items, int Total, IReadOnlyList<SpaceDraftAuthor> Authors);
