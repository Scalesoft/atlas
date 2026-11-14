namespace Atlas.Server.Features.Pages.Commands;

/// <summary>3.6-I: sets the page's own „Publikace“ (<see cref="Queries.SetPagePublicationRequest"/>), or makes it inherit the space's.</summary>
public sealed record SetPagePublication(Guid PageId, Queries.SetPagePublicationRequest Request);
