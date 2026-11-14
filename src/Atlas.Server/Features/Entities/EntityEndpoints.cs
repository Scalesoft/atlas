using System.Text;
using Atlas.Server.Features.Entities.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Entities;

/// <summary>The entity catalog's overview and exports (plan §14.4), for the web and, for AI parity, the personal API alike.</summary>
public static class EntityEndpoints
{
    public static void MapEntities(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/entities/{kind}", async (string kind, string? scope, Guid? id, string? q, int? page, string? identity, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<EntityList>(new ListEntities(kind, scope ?? "instance", id, q, page ?? 1, Identity: identity), ct)));
        var export = group.MapGet("/entities/{kind}/export", async (string kind, string? scope, Guid? id, string? q, string? format, IMessageBus bus, CancellationToken ct) =>
        {
            if (format is not (null or "csv" or "vcard"))
            {
                throw new ApiException(400, "Formát je csv nebo vcard.", "invalid_format");
            }
            var entities = await bus.InvokeAsync<EntityList>(new ListEntities(kind, scope ?? "instance", id, q, All: true), ct);
            return format == "vcard"
                ? Results.File(Encoding.UTF8.GetBytes(ContactExport.VCard(entities)), "text/vcard; charset=utf-8", "atlas-kontakty.vcf")
                : Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ContactExport.Csv(entities))).ToArray(), "text/csv; charset=utf-8", "atlas-kontakty.csv");
        });
        list.Produces<EntityList>().ProducesProblem(400);
        export.Produces(200, contentType: "text/csv").ProducesProblem(400);
        if (!personalApi) return;
        list.WithName("ListEntities")
            .WithSummary("Entities of a kind (contact) the token's owner may read, merged by identity (e-mail, account, directory id, directory object, " +
                "else name with organization), each with its sources: the page (its published revision for a reader, its working one for a writer) " +
                "and heading anchor, or the directory contact: the first 5 and sourceCount, all of them with identity (an identityKey: that entity alone). " +
                "scope instance (default), space, subtree or resource with id; q words; 50 per page.");
        export.WithName("ExportEntities")
            .WithSummary("The same entities (at most 5 000) with all their sources as CSV (default) or vCard 3.0 (format=vcard).");
    }
}
