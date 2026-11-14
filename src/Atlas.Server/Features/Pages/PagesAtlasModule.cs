using System.Reflection;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Modules;
using Wolverine;

namespace Atlas.Server.Features.Pages;

/// <summary>The mandatory wiki: pages and everything around them (content type <c>atlas.page</c>).</summary>
// shortcut: the page features still register in AtlasServices and AtlasEndpoints, so this module contributes only its descriptor and
// the live page channel; move their AddXxx/MapXxx calls here when the first page feature has to be switchable or leaves Atlas.Server.
public sealed class PagesAtlasModule : IAtlasModule
{
    public const string ContentType = "atlas.page";

    public ModuleDescriptor Descriptor { get; } = new("pages", 1, [], [ContentType]);

    public bool Mandatory => true;

    public Assembly MigrationsAssembly => typeof(PagesAtlasModule).Assembly;

    /// <summary>The live page channel: page:{pageId} and the one listener that relays the database's page changes to it.</summary>
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ILiveChannel, PageLiveChannel>();
        services.AddHostedService<PageChangeListener>();
    }

    /// <summary>The pages' own purge is the core's (PagePurge).</summary>
    public void AddPurge(IServiceCollection services)
    {
    }

    public void ConfigureWolverine(WolverineOptions options)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
    }
}
