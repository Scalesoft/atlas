namespace Atlas.Server.Platform.Modules;

/// <summary>GET /api/modules: the enabled modules and the content types they bring, for the web shell's lazy chunks.</summary>
public sealed record ModuleList(IReadOnlyList<ModuleInfo> Modules, IReadOnlyList<string> ContentTypes);

public sealed record ModuleInfo(string Id, int Version);
