using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Contacts.LinkRun.Queries;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Contacts;

/// <summary>The contact as one entity (contacts spec 2026-10-30 §9): the web's routes and, for AI parity, the same under /api/v1 (reads with
/// the read scope, the link and the merge with the write scope: the directory changes for its readers at once). The migration of the
/// contacts written into pages is a WikiAdmin's, the web's only.</summary>
public static class ContactEndpoints
{
    /// <summary>The picture plus the multipart envelope.</summary>
    private const long PhotoRequestBytes = ContactPhotos.MaximumBytes + 64 * 1024;

    public static void MapContacts(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
        // report answers at once (ContactLinkBudgetTests); apply starts the background run and answers with it, which GET follows;
        // cancel ends the running run and answers with the newest.
        app.MapPost("/api/admin/contacts/link", async (string? mode, IMessageBus bus, CancellationToken ct) => (mode ?? "report") switch
            {
                "report" => Results.Ok(await bus.InvokeAsync<ContactLinkReport>(new ReportContactLinks(), ct)),
                "apply" => Results.Accepted("/api/admin/contacts/link", await bus.InvokeAsync<ContactLinkRunView>(new StartContactLinkRun(), ct)),
                "cancel" => await bus.InvokeAsync<ContactLinkRunView?>(new CancelContactLinkRun(), ct) is { } run ? Results.Ok(run) : Results.NoContent(),
                _ => throw new ApiException(400, "Režim je report, apply nebo cancel.", "invalid_argument")
            })
            .RequireAuthorization()
            .Produces<ContactLinkReport>()
            .Produces<ContactLinkRunView>(202)
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(403);
        app.MapGet("/api/admin/contacts/link", async (IMessageBus bus, CancellationToken ct)
                => await bus.InvokeAsync<ContactLinkRunView?>(new GetContactLinkRun(), ct) is { } run ? Results.Ok(run) : Results.NoContent())
            .RequireAuthorization()
            .Produces<ContactLinkRunView>()
            .Produces(204)
            .ProducesProblem(403);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var resolve = group.MapPost("/contacts/resolve", async (ResolveContact request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ResolvedContact>(request, ct)))
            .Produces<ResolvedContact>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Link an editor's contact snapshot to the one directory: the contact with the same e-mail or account (contact_exists, " +
                "contact_conflict warnings; nothing is overwritten), else a new wiki contact (possible_duplicate when only a phone or the name with " +
                "the organization matches). With contactId that contact. Returns contactId, created, the refreshed snapshot and warnings.");
        var match = group.MapPost("/contacts/match", async (MatchContact request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ContactMatchResult>(request, ct)))
            .Produces<ContactMatchResult>().ProducesProblem(400)
            .WithSummary("What the directory has of a contact's person, without a write: strong (same e-mail or account) with its usage, weak candidates, conflicting fields.");
        var live = group.MapPost("/contacts/live", async (LiveContacts request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<LiveContactsResult>(request, ct)))
            .Produces<LiveContactsResult>().ProducesProblem(400)
            .WithSummary("The live contacts behind up to 200 references (merges followed): ok with the contact, hidden, trashed or missing.");
        var duplicates = group.MapGet("/contacts/duplicates", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ContactDuplicateList>(new ListContactDuplicates(), ct)))
            .Produces<ContactDuplicateList>()
            .WithSummary("Pairs of directory contacts that may be one person: reason phone (the same phone), name (name with organization) or similar (the name alone).");
        var merge = group.MapPost("/contacts/merge", async (MergeContacts request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<MergedContacts>(request, ct)))
            .Produces<MergedContacts>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Merge sourceId into targetId: the target takes the missing fields, addresses and phones, the source goes to the trash and redirects to the target.");
        var accounts = group.MapGet("/contacts/accounts", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccountContactSuggestionList>(new ListAccountContactSuggestions(), ct)))
            .Produces<AccountContactSuggestionList>()
            .WithSummary("For a wiki admin: accounts without their directory contact and contacts that may be the same person, computed when read " +
                "(reason email: the account's address, else name: the same name; a contact of another account never). Empty for anyone else.");
        var resolveAccount = group.MapPost("/contacts/accounts", async (ResolveAccountContact request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ResolvedAccountContact>(request, ct)))
            .Produces<ResolvedAccountContact>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("A wiki admin decides a suggested pair: decision link (the contact becomes the account's; the account's own contact is merged " +
                "into it), dismiss (not this person: the account gets its own contact, createdContactId, and is never offered again) or restore with " +
                "contactId = that createdContactId (undo: it goes to the trash).");
        // The contact's logo or photo (package G): one image file in the field file, the same handler as atlas_upsert_contact's photoBase64.
        var setPhoto = group.MapPut("/contacts/{id:guid}/photo", async (Guid id, HttpContext context, IMessageBus bus, CancellationToken ct) =>
            {
                if (!context.Request.HasFormContentType)
                {
                    throw new ApiException(415, "Nahrání vyžaduje multipart/form-data.");
                }
                var form = await context.Request.ReadFormAsync(ct);
                if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
                {
                    throw new ApiException(400, "Vyberte právě jeden soubor v poli file.");
                }
                ContactPhotos.RequireSize(file.Length);
                var content = new byte[(int)file.Length];
                await using (var input = file.OpenReadStream())
                {
                    await input.ReadExactlyAsync(content, ct);
                }
                return Results.Ok(await bus.InvokeAsync<ContactPhotoSet>(new SetContactPhoto(id, content), ct));
            })
            .WithMetadata(new RequestSizeLimitAttribute(PhotoRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = PhotoRequestBytes })
            .Accepts<ContactPhotoUploadForm>("multipart/form-data")
            .Produces<ContactPhotoSet>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(413)
            .WithSummary("Upload the contact's logo or photo (PNG, JPEG or WebP, at most 64 KiB; a 64 px square is enough): its photo field becomes the picture's address.");
        // The URL names the picture's version, so the current one stays in the browser for good; a stale v gets the current one, uncached.
        group.MapGet("/contacts/{id:guid}/photo", async (Guid id, string? v, HttpContext context, IMessageBus bus, CancellationToken ct) =>
            {
                var photo = await bus.InvokeAsync<ContactPhotoContent>(new OpenContactPhoto(id, context.Request.Headers.IfNoneMatch.ToString()), ct);
                context.Response.Headers.CacheControl = v == ContactPhotos.Version(photo.Sha256) ? "private, max-age=31536000, immutable" : "private, no-cache";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                if (photo.Content is null)
                {
                    context.Response.Headers.ETag = '"' + photo.Sha256 + '"';
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                return Results.File(photo.Content, photo.MimeType, entityTag: new EntityTagHeaderValue('"' + photo.Sha256 + '"'));
            })
            .Produces<Stream>(200, "image/png", "image/jpeg", "image/webp").ProducesProblem(404)
            .WithSummary("The contact's uploaded logo or photo, for whoever may read the contact.");
        if (personalApi)
        {
            setPhoto.WithName("SetContactPhoto").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectChange);
            accounts.WithName("AccountContactSuggestions");
            resolveAccount.WithName("ResolveAccountContact").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectChange);
            match.WithName("MatchContact");
            live.WithName("LiveContacts");
            duplicates.WithName("ContactDuplicates");
            resolve.WithName("ResolveContact").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectCreate);
            merge.WithName("MergeContacts").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.ObjectChange);
        }
        else
        {
            // The directory-wide reads throttled per user as search is (/api/v1 has the token's limiter).
            live.RequireRateLimiting("interactive-search");
            duplicates.RequireRateLimiting("interactive-search");
        }
    }
}
