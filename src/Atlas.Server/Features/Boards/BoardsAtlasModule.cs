using System.Reflection;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Modules;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using Wolverine;

namespace Atlas.Server.Features.Boards;

/// <summary>Boards with cards, page cards and connectors, edited live (content type <c>atlas.board</c>); off unless
/// <c>Modules:Boards:Enabled</c>.</summary>
// shortcut: Boards is a feature folder of Atlas.Server (its handlers, mappings and migrations live in this assembly); it moves to
// its own assembly once the platform contracts have one (Atlas 3 plan 5.7).
public sealed class BoardsAtlasModule : IAtlasModule
{
    public const string ContentType = "atlas.board";

    public ModuleDescriptor Descriptor { get; } = new("boards", 1, ["pages"], [ContentType]);

    public bool Mandatory => false;

    public Assembly MigrationsAssembly => typeof(BoardsAtlasModule).Assembly;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<BoardAccess>();
        services.AddScoped<BoardPageTargets>();
        services.AddScoped<BoardObjectTargets>();
        services.AddScoped<Commands.BoardBatchWriter>();
        services.AddSingleton<BoardBroadcasts>();
        services.AddSingleton<ILiveChannel, BoardLiveChannel>();
        // The board as an object type and as a view (F1 plan §2.2, §2.4).
        services.AddScoped<BoardObjectType>();
        services.AddScoped<IObjectType>(provider => provider.GetRequiredService<BoardObjectType>());
        services.AddScoped<IObjectView, BoardObjectView>();
        // The board's trash on the object path (POST /objects/{id}/restore and /purge).
        services.AddScoped<Commands.BoardTreeHandler>();
        services.AddScoped<IObjectTrash>(provider => provider.GetRequiredService<Commands.BoardTreeHandler>());
        // Boards in the pages module's space tree, personal library and search (interaction spec §6).
        services.AddScoped<BoardContent>();
        services.AddScoped<IContentTreeParticipant>(provider => provider.GetRequiredService<BoardContent>());
        services.AddScoped<IContentLibraryParticipant>(provider => provider.GetRequiredService<BoardContent>());
        services.AddScoped<IContentSearchParticipant>(provider => provider.GetRequiredService<BoardContent>());
        services.AddScoped<IContentReferenceParticipant>(provider => provider.GetRequiredService<BoardContent>());
        services.AddScoped<IObjectRelationSource, BoardRelations>();
        // AI parity: the MCP tools exist only with the module on, as Projector's; AddMcpServer only adds what is missing.
        services.AddMcpServer().WithTools<BoardMcpTools>();
    }

    public void AddPurge(IServiceCollection services)
    {
        services.AddScoped<IContentPurgeParticipant, BoardPagePurge>();
    }

    /// <summary>The platform already scans Atlas.Server and, with this module on, no longer excludes its namespace.</summary>
    public void ConfigureWolverine(WolverineOptions options)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapBoards();
    }
}
