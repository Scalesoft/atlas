namespace Atlas.Server.Platform.Modules;

/// <summary>What a module is to the platform: its stable id, contract version, the modules it cannot run without and the
/// content types (<c>atlas.page</c>) it owns.</summary>
public sealed record ModuleDescriptor(string Id, int Version, IReadOnlyList<string> Requires, IReadOnlyList<string> ContentTypes);
