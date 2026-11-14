using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.TreeReferences;

/// <summary>Reference nodes of the space tree (spec 2026-10-27 §13.1 point 3): always on, as the tree they live in.</summary>
public static class TreeReferencesModule
{
    public static IServiceCollection AddTreeReferences(this IServiceCollection services)
    {
        services.AddScoped<TreeReferenceContent>();
        services.AddScoped<IContentTreeParticipant>(provider => provider.GetRequiredService<TreeReferenceContent>());
        services.AddScoped<IContentPurgeParticipant>(provider => provider.GetRequiredService<TreeReferenceContent>());
        services.AddScoped<IObjectRelationSource, TreeReferenceRelations>();
        services.AddMcpServer().WithTools<TreeReferenceMcpTools>();
        return services;
    }
}
