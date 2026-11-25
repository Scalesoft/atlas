using Atlas.Server.Features.PageVerifications;
using Atlas.Server.Features.PageVerifications.Queries;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Wolverine;
using Scalar.AspNetCore;

namespace Atlas.Server.Features.PersonalApi;

public static class PersonalApiEndpoints
{
    /// <summary>Scope read on the channel rest: every GET, search, the document, exports and attachment downloads.</summary>
    public const string ReadPolicy = "PersonalApiRead";
    /// <summary>
    /// Scope write on the channel rest: every change (pages, publication, moves, trash, attachments, tags, personal state). A write is a
    /// live edit within the owner's rights; what readers see follows the page's publication setting.
    /// </summary>
    public const string WritePolicy = "PersonalApiWrite";

    public static string PolicyFor(string scope) => scope == ApiScopes.Write ? WritePolicy : ReadPolicy;

    /// <summary>Scope read on the channel mcp: the /mcp endpoint itself and every read tool.</summary>
    public const string McpReadPolicy = "McpRead";
    /// <summary>Scope write on the channel mcp: every write tool.</summary>
    public const string McpWritePolicy = "McpWrite";

    public static string McpPolicyFor(string scope) => scope == ApiScopes.Write ? McpWritePolicy : McpReadPolicy;

    public static void MapPersonalApi(this IEndpointRouteBuilder app)
    {
        MapProfile(app);
        MapAdministration(app);
        MapDocument(app);
        MapVersionOne(app);
    }

    private static void MapProfile(IEndpointRouteBuilder app)
    {
        var profile = app.MapGroup("/api/profile/api-tokens").RequireAuthorization();
        profile.MapGet("", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ApiTokenProfile>(new ListApiTokens(), ct)));
        profile.MapPost("", async (CreateApiToken request, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<CreatedApiToken>(new IssueApiToken(request), ct);
            return Results.Created($"/api/profile/api-tokens/{result.Token.Id}", result);
        }).RequireRateLimiting("authentication");
        profile.MapDelete("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeApiToken(id), ct);
            return Results.NoContent();
        });
    }

    private static void MapDocument(IEndpointRouteBuilder app)
    {
        app.MapOpenApi().RequireAuthorization();
        app.MapScalarApiReference().RequireAuthorization();
        app.MapOpenApi("/api/v1/openapi.json").RequireAuthorization(ReadPolicy).RequireRateLimiting("personal-api");
        // Signed-in users can download the same specification from their profile without
        // putting a personal token into a URL or browser storage.
        app.MapOpenApi("/api/profile/api-document").RequireAuthorization();
    }

    private static void MapAdministration(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/users/{id:guid}/api-access", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ApiPermissionDto>(new GetApiAccess(id), ct))).RequireAuthorization();
        app.MapPut("/api/admin/users/{id:guid}/api-access", async (Guid id, ChangeApiPermission request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ApiPermissionDto>(new SetApiAccess(id, request), ct))).RequireAuthorization();
        app.MapGet("/api/admin/api-tokens", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<IReadOnlyList<AdminApiToken>>(new ListAdminApiTokens(), ct))).RequireAuthorization();
        app.MapGet("/api/admin/api-tokens/summary", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ApiTokensSummary>(new GetApiTokensSummary(), ct))).RequireAuthorization();
        app.MapPost("/api/admin/api-tokens/levels-review", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ApiTokensSummary>(new ConfirmApiLevelsReview(), ct))).RequireAuthorization();
        app.MapDelete("/api/admin/api-tokens/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeUserApiToken(id), ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static void MapVersionOne(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(ReadPolicy).RequireRateLimiting("personal-api");
        api.MapGet("/spaces", async (int? offset, int? limit, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<ApiList<ApiSpace>>(new ListApiSpaces(offset, limit), ct)))
            .WithName("ListAccessibleSpaces").WithSummary("List spaces available to the token owner.").Produces<ApiList<ApiSpace>>();
        api.MapGet("/spaces/{spaceId:guid}/pages", async (Guid spaceId, int? offset, int? limit, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<ApiList<ApiPageEntry>>(new ListApiSpacePages(spaceId, offset, limit), ct)))
            .WithName("ListSpacePages").WithSummary("List accessible current pages with a stable ID and parent ID.").Produces<ApiList<ApiPageEntry>>();
        api.MapGet("/pages/{id:guid}", async (Guid id, string? format, IMessageBus bus, CancellationToken ct) =>
            {
                if (format is not (null or "" or "markdown"))
                {
                    throw new ApiException(400, "format je \"markdown\", nebo chybí.", "invalid_argument");
                }
                var page = await bus.InvokeAsync<PageDto>(new GetPage(id), ct) with
                {
                    Verification = await bus.InvokeAsync<PageVerificationView>(new GetPageVerification(id), ct)
                };
                return Results.Ok(format == "markdown" ? page with { Source = PageMarkdownSource.From(page) } : page);
            })
            .WithName("GetPage").WithSummary("Get a page according to the owner's current access. With format=markdown the answer adds source: the page as " +
                "Markdown in the editor's dialect with component placeholders ⟦c:N⟧ (source.components describes each) and images of the page's attachments " +
                "as ![alt](/api/attachments/{id}/content); edit it and send it back to PUT /api/v1/pages/{id} with version as expectedVersion. The page's own markdown field is the read-only stored " +
                "projection for search and export; only source.markdown is meant to be edited.")
            .Produces<PageDto>().ProducesProblem(400).ProducesProblem(404);
        api.MapPost("/pages", async (ApiCreatePageRequest request, PageMarkdownCreate create, CancellationToken ct) =>
        {
            var result = await create.CreateAsync(request, ct);
            return Results.Created($"/api/v1/pages/{result.Id}", result);
        }).RequireAuthorization(WritePolicy).AuditApiWrite(ApiOperations.PageCreate).WithMetadata(PersonalApiConcurrency.MarkdownWrites).WithName("CreatePage").WithSummary("Create a draft from markdown in the " +
            "editor's dialect; the server converts it (no placeholders; images ![alt](/api/attachments/{id}/content) only of attachments of the page, so " +
            "upload them and add them with a save). Requires Writer role in its space. With templatePageId the markdown is appended to the template's copy. " +
            "contentJson is refused with 400 content_json_not_accepted. warnings lists what the conversion changed.")
            .Produces<PageDto>(201).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(413);
        api.MapPut("/pages/{id:guid}", async (Guid id, ApiSavePageRequest request, PageMarkdownSaveRunner runner, CancellationToken ct) =>
                Results.Ok(await runner.SaveAsync(id, request, ct)))
            .LiveWrite(wholeDocument: true).RequireAuthorization(WritePolicy).AuditApiWrite(ApiOperations.PageSave).WithMetadata(PersonalApiConcurrency.MarkdownWrites).WithName("SavePage").WithSummary("Replace the working revision with " +
            "markdown: the whole text of GET /api/v1/pages/{id}?format=markdown, edited. Each placeholder ⟦c:N⟧ brings its component back; a placeholder " +
            "left out removes its component only when removeComponents lists its number N, otherwise 409 components_removed and nothing is saved " +
            "(a listed N that is still in the markdown or not on the page: 400 invalid_argument). Unchanged blocks keep their stored JSON; an edited " +
            "block keeps its alignment, cell colours, column widths, row heights and list numbering where it is clear where they go (rows, columns and " +
            "list items by unchanged content, the rest by position only when as many remain; a split paragraph on every part); an edit that would still drop any returns 409 formatting_lost (field blocks) " +
            "unless resetFormatting is true. Images " +
            "![alt](/api/attachments/{id}/content) and links [text](/api/attachments/{id}/download) must be attachments of this page (404 " +
            "attachment_not_found, 400 attachment_not_image); other images become links. title is optional. contentJson is refused with 400 " +
            "content_json_not_accepted. A stale expectedVersion returns 409 version_conflict, unsaved live edits by other users 409 collaboration_unsaved.")
            .Produces<PageDto>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
        api.MapPost("/pages/{id:guid}/publish", async (Guid id, PublishPageRequest request, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<PageDto>(new PublishPage(id, request), ct)))
            .RequireAuthorization(WritePolicy).AuditApiWrite(ApiOperations.PagePublish).WithName("PublishPage").WithSummary("Publish the expected working revision atomically with the search index. The optional changeSummary describes what the publication changed: one line, at most 500 characters.").Produces<PageDto>().ProducesProblem(409);
        api.MapGet("/pages/{id:guid}/history", async (Guid id, int? beforeVersion, int? limit, Guid? group, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<PageHistoryList>(new GetPageHistory(id, beforeVersion, limit ?? 20, group), ct)))
            .WithName("ListPageHistory").WithSummary("List versions newest first with an exclusive beforeVersion cursor: autosave checkpoints are grouped into versions (a pause of 30 minutes, at most 4 hours, a new day in Europe/Prague, a publication, a restore, a named version or a change with AI start a new one). Each version is its newest revision with what it changed (changes: lines added and removed, changed blocks, sections, requirement states, contacts, attachments, title; null before 3.6), its contributors, isAi with the channel and token of an AI change, and previousRevisionId to compare with. group lists the revisions (steps) of one version by its groupId. Requires the owner's Writer role.").Produces<PageHistoryList>().ProducesProblem(400).ProducesProblem(404);
        api.MapGet("/pages/{id:guid}/history/{revisionId:guid}", async (Guid id, Guid revisionId, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<PageHistoryRevision>(new GetPageRevision(id, revisionId), ct)))
            .WithName("GetPageRevision").WithSummary("Read a historical revision. Requires the owner's Writer role.").Produces<PageHistoryRevision>().ProducesProblem(404);
        api.MapPost("/pages/{id:guid}/restore", async (Guid id, RestorePageRequest request, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<PageDto>(new RestorePage(id, request), ct)))
            .RequireAuthorization(WritePolicy).AuditApiWrite(ApiOperations.PageRestore).WithName("RestorePageRevision").WithSummary("Restore as a new working revision using expectedVersion. Published content remains unchanged. Restoring the published revision discards the unpublished changes: the page has nothing left to publish and the discarded state stays in the history. Unsaved live edits by other users return 409 collaboration_unsaved.").Produces<PageDto>().ProducesProblem(409).ProducesProblem(404);
        api.MapPost("/pages/{id:guid}/versions", async (Guid id, NameVersionRequest request, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<NamedVersion>(new NameVersion(id, request.ExpectedVersion, request.Label), ct)))
            .RequireAuthorization(WritePolicy).AuditApiWrite(ApiOperations.PageVersionName).WithName("NamePageVersion").WithSummary("Save a version: the working revision at expectedVersion becomes a named point of the history (optional label, one line, at most 500 characters) and the next change starts a new version. The content does not change. A stale expectedVersion returns 409 version_conflict.").Produces<NamedVersion>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        api.MapGet("/search", async (string? q, Guid? spaceId, int? offset, int? limit, ISemanticQuery semantic, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.SearchAsync(semantic,
                    // Pinned to the published scope: this route is documented as SearchPublishedPages, so the web UI's unified
                    // default must not silently widen a token's results to drafts. Hybrid like the web (O11), additive fields only.
                    new Server.Features.Search.Queries.Search(q, spaceId, offset ?? 0, limit ?? 20, new SearchOptions(Content: "published")), ct)))
            .RequireRateLimiting("search").WithName("SearchPublishedPages").WithSummary("Typo-tolerant search of accessible published pages and spaces. Filter by spaceId; follow nextOffset (limit 1–50). Quoted phrases match ordered words. Headings link only to published persistent anchors. With the wiki's semantic search available, pages matching by meaning come too (semantic: true) and hits may carry a passage with its section anchor; both are omitted otherwise. didYouMean suggests a readable title for a weak query.").Produces<SearchResponse>().ProducesProblem(503);
    }
}
