namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>The module page (W3): the switch, the endpoint address for the client configuration and the CAS version (null until a
/// WikiAdmin first saves; the module is then off).</summary>
public sealed record McpSettingsView(bool Enabled, string Url, string? Version);

public sealed record SaveMcpRequest(bool Enabled, string? ExpectedVersion);
