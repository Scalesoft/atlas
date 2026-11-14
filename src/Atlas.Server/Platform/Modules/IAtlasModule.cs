using System.Reflection;
using Wolverine;

namespace Atlas.Server.Platform.Modules;

/// <summary>
/// A module of the distribution (Atlas 3 plan, section 5.2). Every module is compiled in; configuration switches it on at
/// startup (<c>Modules:{Id}:Enabled</c>), and a change of the set needs a restart. Only an enabled module registers services,
/// Wolverine handlers and endpoints. A disabled module's handlers are the types in the namespace of its module class and
/// below; the platform keeps them out of Wolverine discovery (ConfigureAtlasWolverine), so they cannot run even indirectly.
/// </summary>
public interface IAtlasModule
{
    ModuleDescriptor Descriptor { get; }

    /// <summary>Always on: the switch cannot turn it off, and configuring it off stops the startup.</summary>
    bool Mandatory { get; }

    /// <summary>Scanned for migrations whether the module is enabled or not: a database where it once ran must still start.</summary>
    Assembly MigrationsAssembly { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Its part of a permanent delete (IContentPurgeParticipant), registered for every module of the distribution, enabled or
    /// not (AddAtlasPlatform): its rows outlive a switch-off, so the purge of a page or space must reach them (ARCH3-2).</summary>
    void AddPurge(IServiceCollection services);

    /// <summary>Where an enabled module adds handlers from an assembly the platform does not scan.</summary>
    void ConfigureWolverine(WolverineOptions options);

    void MapEndpoints(IEndpointRouteBuilder app);
}
