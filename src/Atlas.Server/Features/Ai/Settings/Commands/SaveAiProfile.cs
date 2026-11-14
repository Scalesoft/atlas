using Atlas.Server.Features.Ai.Settings.Contracts;

namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>The request carries a write-only API key; its ToString is redacted, so neither record prints it.</summary>
public sealed record SaveAiProfile(string Kind, SaveAiProfileRequest Request);
