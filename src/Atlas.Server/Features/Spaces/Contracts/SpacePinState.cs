namespace Atlas.Server.Features.Spaces.Contracts;

/// <summary>The request asks for a pin, the response answers the effective one.</summary>
public sealed record SpacePinState(bool Pinned);
