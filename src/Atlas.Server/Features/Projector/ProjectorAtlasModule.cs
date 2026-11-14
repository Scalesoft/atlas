using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Mcp;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Modules;
using Wolverine;

namespace Atlas.Server.Features.Projector;

/// <summary>Projector v1 (spec 2026-10-27): requirement blocks in pages, their catalog, overview and the copy into a new deployment;
/// off unless <c>Modules:Projector:Enabled</c>. Its tables and the index triggers exist either way (migration 2026102800).</summary>
// shortcut: a feature folder of Atlas.Server like Boards; it moves to its own assembly with the platform contracts (Atlas 3 plan 5.7).
public sealed class ProjectorAtlasModule : IAtlasModule
{
    public ModuleDescriptor Descriptor { get; } = new("projector", 1, ["pages"], []);

    public bool Mandatory => false;

    public Assembly MigrationsAssembly => typeof(ProjectorAtlasModule).Assembly;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<RequirementIndex>();
        // The full rebuild of the requirement index (migration 2026110870, every restore); the outbox and the reads keep it otherwise.
        services.AddJob<ProjectorIndexJob>();
        services.AddScoped<ProjectorCatalogReader>();
        services.AddScoped<RequirementPageWriter>();
        services.AddScoped<ProjectorOpenings>();
        services.AddScoped<IPageCatalogMark, ProjectorCatalogMark>();
        services.AddScoped<Queries.SpaceRequirementRows>();
        // Plan 3.1 M4b: dependencies across pages, the party's contact (through the contacts' identity service) and both as uses.
        services.AddScoped<CrossPageRequirements>();
        services.AddScoped<RequirementLinks>();
        services.TryAddScoped<Atlas.Server.Features.Contacts.Commands.ResolveContactHandler>();
        services.AddScoped<Atlas.Server.Platform.Resources.IObjectRelationSource, ProjectorRelations>();
        // The copy of an own-access page sets its access through the page access commands (review A2-1).
        services.TryAddScoped<SetPageAccessModeHandler>();
        services.TryAddScoped<SetPageGrantHandler>();
        services.TryAddScoped<PageGrantCopier>();
        // The presets are the template list's (its listing rule and order).
        services.TryAddScoped<Atlas.Server.Features.PageTemplates.ListPageTemplatesHandler>();
        // A factory registration: Wolverine resolves it from the container instead of inlining a new cache into each handler.
        services.AddSingleton(_ => new ProjectorCatalogCache());
        // The MCP tools exist only with the module on: never listed, and a call is an unknown tool (spec §6). AddMcpServer only adds
        // what is missing, so the MCP module's own registration (AddMcp) stays as it is.
        services.AddMcpServer().WithTools<ProjectorMcpTools>();
    }

    public void AddPurge(IServiceCollection services)
    {
        services.AddScoped<Atlas.Server.Platform.Resources.IContentPurgeParticipant, ProjectorPurge>();
    }

    /// <summary>The platform already scans Atlas.Server and, with this module on, no longer excludes its namespace.</summary>
    public void ConfigureWolverine(WolverineOptions options)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapProjector();
    }
}
