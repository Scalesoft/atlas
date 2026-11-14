namespace Atlas.Server.Features.Spaces.Contracts;

/// <summary>Envelope: a handler returning a bare collection would look like cascading messages to Wolverine. The endpoint returns Items.</summary>
public sealed record AccessibleSpaces(IReadOnlyList<SpaceListItem> Items);
