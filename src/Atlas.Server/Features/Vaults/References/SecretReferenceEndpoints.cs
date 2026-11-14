using Wolverine;

namespace Atlas.Server.Features.Vaults.References;

/// <summary>
/// Password references in the editor (WP4 V6): the picker's search and the metadata of one referenced secret. Both answer only
/// what the caller may Discover, never a value; /api/secrets is no content route of RequestErrorHandling, so a failure logs only its type.
/// </summary>
public static class SecretReferenceEndpoints
{
    public static void MapSecretReferences(this IEndpointRouteBuilder app)
    {
        var secrets = app.MapGroup("/api/secrets").RequireAuthorization();
        secrets.MapGet("/search", async (string? q, Guid? spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<SecretReferenceView>>(new SearchSecretReferences(q, spaceId), ct)))
            .Produces<IReadOnlyList<SecretReferenceView>>();
        secrets.MapGet("/{id:guid}/reference", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SecretReferenceView>(new GetSecretReference(id), ct)))
            .Produces<SecretReferenceView>()
            .ProducesProblem(404);
    }
}
