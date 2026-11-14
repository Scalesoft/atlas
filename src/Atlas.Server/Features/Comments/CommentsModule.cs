using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Comments;

/// <summary>Comments on every object (comments spec): one flat timeline per object, decided through the object type registry,
/// so a new object type has comments without any change here.</summary>
public static class CommentsModule
{
    public static IServiceCollection AddComments(this IServiceCollection services)
    {
        services.AddScoped<CommentGate>();
        services.AddScoped<CommentNotices>();
        services.AddScoped<IContentSearchParticipant, CommentSearch>();
        return services;
    }
}
