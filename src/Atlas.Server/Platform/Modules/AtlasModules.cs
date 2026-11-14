using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Projector;

namespace Atlas.Server.Platform.Modules;

/// <summary>The modules of this distribution: the one place that lists them (Atlas 3 plan 5.3).</summary>
public static class AtlasModules
{
    public static IReadOnlyList<IAtlasModule> All { get; } = [new PagesAtlasModule(), new BoardsAtlasModule(), new ProjectorAtlasModule()];
}
