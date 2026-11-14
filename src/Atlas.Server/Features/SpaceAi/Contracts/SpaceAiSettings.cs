namespace Atlas.Server.Features.SpaceAi.Contracts;

/// <summary>Version is null until the space's inclusion is first changed; the space is then included by default.</summary>
public sealed record SpaceAiSettingsView(bool Included, string? Version);

/// <summary>Included is required; ExpectedVersion is the Version last read (null for a space never changed).</summary>
public sealed record SaveSpaceAiSettingsRequest(bool? Included, string? ExpectedVersion);
