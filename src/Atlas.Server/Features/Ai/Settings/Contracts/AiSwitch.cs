namespace Atlas.Server.Features.Ai.Settings.Contracts;

/// <summary>The master switch. Off and Version null until a WikiAdmin first saves (AI-24).</summary>
public sealed record AiSwitchView(bool Enabled, string? Version);

public sealed record SaveAiSwitchRequest(bool Enabled, string? ExpectedVersion);
