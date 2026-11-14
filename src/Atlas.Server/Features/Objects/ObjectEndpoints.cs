using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Objects;

/// <summary>The one object path (F1 plan §2.3). Cookie API: CSRF and Problem Details come from the pipeline, the rules from the
/// handlers. For AI parity the same routes for personal tokens under /api/v1: reads with the read scope, every write with the publish
/// scope (a shared or wiki object changes for its readers at once, as a board), audited.</summary>
public static class ObjectEndpoints
{
    public static void MapObjects(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
        // The trash (unified trash spec §2): the list and the restore for the personal API too; the permanent delete is the web's only
        // (rule 6: an administrator's action, as access changes).
        MapTrash(app.MapGroup("/api").RequireAuthorization(), false);
        MapTrash(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
        app.MapPost("/api/objects/{objectId:guid}/purge", async (Guid objectId, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new PurgeObject(objectId), ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .Produces(204)
            .ProducesProblem(404);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/objects", async (string? type, string? scope, Guid? spaceId, string? q, int? page, string[]? name, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ObjectList>(new ListObjects(type, scope, spaceId, q, page ?? 1, name), ct)))
            .Produces<ObjectList>()
            .WithSummary("The object library: objects of a stored type (atlas.contact, atlas.note) the reader may read, newest change first; scope private " +
                "lists the reader's own; name (repeatable) keeps those titled exactly so.");
        var create = group.MapPost("/objects", async (CreateObjectRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                var created = await bus.InvokeAsync<ObjectDto>(new CreateObject(request.TypeId, request.Scope, request.SpaceId, request.Data), ct);
                return Results.Created($"/api/objects/{created.Id}", created);
            })
            .Produces<ObjectDto>(201)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .WithSummary("Create an object of a stored type in its scope: a private note is { typeId: atlas.note, scope: private, data: { title, text } }.");
        var get = group.MapGet("/objects/{objectId:guid}", async (Guid objectId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ObjectDto>(new GetObject(objectId), ct)))
            .Produces<ObjectDto>()
            .ProducesProblem(404)
            .WithSummary("An object the reader may read: envelope, canEdit, title and data.");
        var change = group.MapPost("/objects/{objectId:guid}/changes", async (Guid objectId, ObjectChangeRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ObjectChangeResult>(new ApplyObjectChange(objectId, request.ChangeId, request.ExpectedVersion, request.Payload), ct)))
            .Produces<ObjectChangeResult>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithSummary("Apply one change once per changeId, onto expectedVersion when given: { fields: { … } } for a contact or a note (title, text).");
        var trash = group.MapDelete("/objects/{objectId:guid}", async (Guid objectId, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new TrashObject(objectId), ct);
                return Results.NoContent();
            })
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .WithSummary("Move the object to the trash; a private object only by its owner.");
        if (personalApi)
        {
            list.WithName("ListObjects");
            get.WithName("GetObject");
            create.WithName("CreateObject").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectCreate);
            change.WithName("ChangeObject").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectChange);
            trash.WithName("TrashObject").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectTrash);
        }
    }

    private static void MapTrash(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/objects/trash", async (string? type, string? scope, Guid? spaceId, int? page, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ObjectTrashList>(new ListObjectTrash(type, scope, spaceId, page ?? 1), ct)))
            .Produces<ObjectTrashList>()
            .WithSummary("Trashed objects of the types the platform stores (contacts, notes), newest first, 50 per page; only those the caller may restore: " +
                "a wiki one for who writes the type's wiki objects, a space one for the space's Admin, a private one for its owner.");
        var restore = group.MapPost("/objects/{objectId:guid}/restore", async (Guid objectId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ObjectChangeResult>(new RestoreObject(objectId), ct)))
            .Produces<ObjectChangeResult>()
            .ProducesProblem(404)
            .WithSummary("Bring a trashed object back as it was, with a new version. The same rights as the trash list.");
        if (personalApi)
        {
            list.WithName("ListObjectTrash");
            restore.WithName("RestoreObject").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectRestore);
        }
    }
}
