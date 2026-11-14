namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <summary>What a space says, saved or still in a form: the automatic image is picked from these three.</summary>
public sealed record SpaceCoverMatchRequest(string? Name, string? Description, string? Icon);
