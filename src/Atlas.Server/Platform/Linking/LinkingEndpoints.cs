using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Features.Linking.Queries;
using Wolverine;

namespace Atlas.Server.Platform.Linking;

/// <summary>
/// The linking of content (P15 „Provázání samo od sebe“, audit engine §6), for the web's overview „Nepropojené zmínky“ and, for AI
/// parity, the personal API alike: the space's unresolved chips and suggestions with a cursor, one occurrence linked or kept as text,
/// a page linked whole, and (API only) the recognition of a text or a page.
/// </summary>
public static class LinkingEndpoints
{
    public static void MapLinking(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var unlinked = group.MapGet("/spaces/{spaceId:guid}/unlinked", async (Guid spaceId, string? cursor, int? limit, Guid? pageId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<UnlinkedList>(new ListUnlinked(spaceId, cursor, limit, pageId), ct)))
            .Produces<UnlinkedList>().ProducesProblem(400).ProducesProblem(404);
        var resolve = group.MapPost("/pages/{pageId:guid}/references", async (Guid pageId, ResolveReferenceRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ReferenceWriteResult>(new ResolveReference(pageId, request), ct)))
            .LiveWrite().Produces<ReferenceWriteResult>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        var link = group.MapPost("/pages/{pageId:guid}/link", async (Guid pageId, LinkPageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ReferenceWriteResult>(new LinkPage(pageId, request.ExpectedVersion), ct)))
            .LiveWrite().Produces<ReferenceWriteResult>().ProducesProblem(404).ProducesProblem(409);
        if (!personalApi)
        {
            return;
        }
        group.MapPost("/recognize", async (Recognize request, IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<RecognizeResult>(request, ct)))
            .Produces<RecognizeResult>().ProducesProblem(400).ProducesProblem(404).WithName("Recognize")
            .WithSummary("What the linking engine recognizes (no write): with pageId the page's unresolved chips and suggestions (the revision the owner " +
                "edits, else the published one) with the ordinal POST /api/v1/pages/{id}/references takes; with spaceId and text that text's hits with " +
                "offsets. decision is what link auto does: chip (one whole name of one person), unresolved (several people of the name) or suggest " +
                "(aliases, initials, inflected forms, page titles: only offered). Candidates are only what the token's owner may read.");
        unlinked.WithName("ListUnlinkedReferences")
            .WithSummary("The space's unresolved chips and suggested plain-text mentions of people, pages and YouTrack issues on pages the owner may " +
                "read (the revision they edit, else the published one); pageId narrows it to one page. Each has the page's version " +
                "(expectedVersion of a resolve) and context, the block's text around it. Follow nextCursor (limit 1–200, default 50).");
        resolve.WithName("ResolveReference").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageSave)
            .WithSummary("Decide one occurrence of GET /api/v1/spaces/{id}/unlinked at expectedVersion: choice {\"targetId\": contact, account or " +
                "page id} links it, \"text\" turns an unresolved chip into its text (plain text stays as it is and is offered again: only the " +
                "page says what is linked, nothing is remembered beside it). text must be the occurrence's text (else 409 reference_changed). A change that only turns text into a " +
                "chip goes straight into the published version when there is no pending draft (published: true, undo by restoring the previous " +
                "revision), else into the draft. Idempotent.");
        link.WithName("LinkPage").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageSave)
            .WithSummary("Link a page at expectedVersion as typing would: every whole name of one person the owner may read becomes a chip, of " +
                "several people an unresolved chip; aliases, initials, inflected forms and page titles are only reported (links.suggested with " +
                "ordinals). Link-only, so without a pending draft it goes straight into the published version (published: true). A second run " +
                "links nothing twice.");
    }
}
