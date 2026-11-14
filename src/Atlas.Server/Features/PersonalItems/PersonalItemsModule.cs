using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.PersonalItems.Contracts;
using Atlas.Server.Features.PersonalItems.Domain;
using Atlas.Server.Features.PersonalItems.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Objects;
using FluentMigrator;
using Wolverine;

namespace Atlas.Server.Features.PersonalItems;

[Migration(2026102990, "Můj prostor reads the owner's private objects by an index")]
public sealed class PersonalOwnerIndexMigration() : EmbeddedSchemaMigration("2026102990_personal_owner_index.sql");

/// <summary>
/// The personal space (spec 2026-10-29): the private note as an object type, sharing one item (the access kind <c>item</c>), and the lists
/// Můj prostor and Sdíleno se mnou. A note lives in the envelope (ContentResource, ObjectChange) and a share in ResourceGrant, so the
/// platform's, the objects' and the access backups already hold them; it has no table.
/// </summary>
public static class PersonalItemsModule
{
    public static IServiceCollection AddPersonalItems(this IServiceCollection services)
    {
        services.AddScoped<NoteObjectType>();
        services.AddScoped<IObjectType>(provider => provider.GetRequiredService<NoteObjectType>());
        services.AddScoped<PersonalItemTargets>();
        services.AddScoped<SetItemGrantHandler>();
        services.AddScoped<IAccessKind, PersonalItemAccessKind>();
        services.AddMcpServer().WithTools<PersonalItemsMcpTools>();
        return services;
    }

    /// <summary>The web's cookie API under /api and, for AI parity, the same reads for personal tokens under /api/v1 (read scope).</summary>
    public static void MapPersonalItems(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var mine = group.MapGet("/personal/items", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<PersonalItemDto>>(new ListPersonalItems(), ct)))
            .Produces<IReadOnlyList<PersonalItemDto>>()
            .WithSummary("Můj prostor: the caller's private notes, newest change first, with a plain-text excerpt and their share counts; GET /objects/{id} reads one whole.");
        var shared = group.MapGet("/personal/shared", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<PersonalItemDto>>(new ListSharedWithMe(), ct)))
            .Produces<IReadOnlyList<PersonalItemDto>>()
            .WithSummary("Sdíleno se mnou: others' notes shared with the caller or its groups now, with owner, level and end of access.");
        // The trash of Můj prostor is the object trash (GET /objects/trash?scope=private): one list, one restore and purge route (ARCH1-2).
        if (personalApi)
        {
            mine.WithName("ListPersonalItems");
            shared.WithName("ListSharedWithMe");
        }
    }
}
