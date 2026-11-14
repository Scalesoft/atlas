namespace Atlas.Server.Features.ModuleSettings.Contracts;

/// <summary>Version is null until a WikiAdmin first saves; the switch is then on by default.</summary>
public sealed record EditorAssistSettingsView(bool Enabled, string? Version);

public sealed record SaveEditorAssistRequest(bool Enabled, string? ExpectedVersion);
