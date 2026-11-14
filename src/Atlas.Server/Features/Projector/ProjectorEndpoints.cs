using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Wolverine;

namespace Atlas.Server.Features.Projector;

public sealed record SetCatalogPageRequest(bool IsCatalog);

public sealed record CreateDeploymentRequest(Guid SpaceId, Guid? ParentId, string Title);

public sealed record UpdateRequirementRequest(RequirementChange Fields, int ExpectedVersion) : Collaboration.IExpectedVersion;

public sealed record UpdateRequirementsRequest(IReadOnlyList<RequirementBlockChange>? Updates, int ExpectedVersion) : Collaboration.IExpectedVersion;

public sealed record InsertRequirementsRequest(IReadOnlyList<string> Keys, RequirementPosition? Position, int ExpectedVersion, IReadOnlyList<string>? ModuleKeys = null) : Collaboration.IExpectedVersion;

public sealed record CreateRequirementRequest(RequirementChange? Fields, RequirementPosition? Position, int ExpectedVersion) : Collaboration.IExpectedVersion;

public sealed record InsertFromDeploymentRequest(Guid SourcePageId, IReadOnlyList<string>? Keys, RequirementPosition? Position, int ExpectedVersion) : Collaboration.IExpectedVersion;

public sealed record MoveRequirementRequest(RequirementPosition Position, int ExpectedVersion) : Collaboration.IExpectedVersion;

/// <summary>
/// The Projector API (spec §4, §6); mapped only with the module on. The web's cookie API under /api/projector (CSRF and Problem Details
/// from the pipeline; its space-wide reads throttled per user as search is); the same reads and writes for personal tokens under
/// /api/v1/projector with the scopes of /api/v1/pages (read; draft for every write, audited, and bounded per token as the Markdown writes
/// are, since each parses and validates a whole page), plus the block commands, which the web does not need (it writes blocks through
/// the live document). The rules are the handlers'. Endpoint names are unique, so the web's carry the prefix Web.
/// </summary>
public static class ProjectorEndpoints
{
    /// <summary>The web's overview leaves out a cell's empty attributes (null, "", []), most of a matrix of a thousand cells; overviewApi.ts
    /// fills them back in. The /api/v1 answer keeps every attribute.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions CompactCells = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                type =>
                {
                    if (type.Type != typeof(Requirement)) return;
                    foreach (var property in type.Properties)
                        property.ShouldSerialize = (_, value) => value is not (null or "" or System.Collections.ICollection { Count: 0 });
                }
            }
        }
    };

    public static void MapProjector(this IEndpointRouteBuilder app)
    {
        var web = app.MapGroup("/api/projector").RequireAuthorization();
        // The web resolves the catalog itself (requirementResolver.ts, parity fixture); /api/v1 keeps the resolve for tokens (review C2-6).
        MapShared(web, "Web", read => read.RequireRateLimiting("interactive-search"));
        // The web's dependency picker searches as the user types; tokens query the space with projector_query / its filters.
        web.MapGet("/spaces/{spaceId:guid}/requirements/search", async (Guid spaceId, string? q, Guid? excludePageId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<RequirementRow>>(new SearchSpaceRequirements(spaceId, q, excludePageId), ct)))
            .WithName("WebSearchSpaceRequirements")
            .WithSummary("At most 50 keyed requirements of the space's deployment pages the user may read whose title, key or page title holds every word of q; excludePageId leaves that page out before the limit.")
            .Produces<IReadOnlyList<RequirementRow>>().ProducesProblem(400).ProducesProblem(404)
            .RequireRateLimiting("interactive-search");
        MapVersionOne(app.MapGroup("/api/v1/projector").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"));
    }

    private static (RouteHandlerBuilder SetCatalog, RouteHandlerBuilder CreateDeployment) MapShared(RouteGroupBuilder group, string prefix,
        Action<RouteHandlerBuilder> spaceRead)
    {
        spaceRead(group.MapGet("/spaces/{spaceId:guid}/catalog", async (Guid spaceId, bool? bodies, string[]? keys, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ProjectorCatalog>(new GetProjectorCatalog(spaceId, bodies ?? true, keys is { Length: > 0 } ? keys : null), ct)))
            .WithName(prefix + "GetProjectorCatalog")
            .WithSummary("The requirement definitions of the space's published catalog pages the owner may read; bodies=false leaves out each block's contentJson, keys=… (repeated) keeps only those keys.")
            .Produces<ProjectorCatalog>().ProducesProblem(404));
        group.MapGet("/pages/{pageId:guid}/requirements", async (Guid pageId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageRequirements>(new GetPageRequirements(pageId), ct)))
            .WithName(prefix + "GetPageRequirements")
            .WithSummary("A page's requirement blocks with their summary and what each waits on; the working revision for an editor (with the version block writes expect), the publication otherwise.")
            .Produces<PageRequirements>().ProducesProblem(404);
        group.MapGet("/pages/{pageId:guid}/requirements/{blockId}/history", async (Guid pageId, string blockId, int? limit, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementHistory>(new GetRequirementHistory(pageId, blockId, limit ?? 50), ct)))
            .WithName(prefix + "GetRequirementHistory")
            .WithSummary("A requirement block's history, newest first (limit 1–200, default 50), read from the revision the owner is shown (the working " +
                "one for an editor of the page, the publication otherwise): the dated lines of its notes (kind note) and the state changes of the page's " +
                "versions with who saved them when (kind state).")
            .Produces<RequirementHistory>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/pages/{pageId:guid}/requirements/{blockId}/dependents", async (Guid pageId, string blockId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementDependents>(new GetRequirementDependents(pageId, blockId), ct)))
            .WithName(prefix + "GetRequirementDependents")
            .WithSummary("What a requirement blocks on other pages: the blocks the owner may read whose requires name it as key@pageId.")
            .Produces<RequirementDependents>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/pages/{pageId:guid}/requirements/cycle", async (Guid pageId, string key, string[]? requires, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementCycle>(new CheckRequirementCycle(pageId, key, requires ?? []), ct)))
            .WithName(prefix + "CheckRequirementCycle")
            .WithSummary("The cycle the block with this key would close by requiring requires=… (repeated; key@pageId for another page's block) through other pages; path null when none.")
            .Produces<RequirementCycle>().ProducesProblem(400).ProducesProblem(404);
        // Many pages in one read: throttled as the space-wide reads.
        spaceRead(group.MapGet("/prerequisites", async (string[]? refs, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<RequirementPrerequisite>>(new GetRequirementPrerequisites(refs ?? []), ct)))
            .WithName(prefix + "GetRequirementPrerequisites")
            .WithSummary("Requirements of other pages by reference (refs=key@pageId, repeated, at most 500): title, state and page of each the owner may read; " +
                "readable false and nothing else for one they may not.")
            .Produces<IReadOnlyList<RequirementPrerequisite>>().ProducesProblem(400));
        spaceRead(group.MapGet("/spaces/{spaceId:guid}/overview", async (Guid spaceId, string? filter, IMessageBus bus, CancellationToken ct)
                => prefix == "Web"
                    ? Results.Json(await bus.InvokeAsync<RequirementOverview>(new GetProjectorOverview(spaceId, filter), ct), CompactCells)
                    : Results.Ok(await bus.InvokeAsync<RequirementOverview>(new GetProjectorOverview(spaceId, filter), ct)))
            .WithName(prefix + "GetProjectorOverview").WithSummary("Pages × requirement keys of the space; filter missing (default), inbound-missing or all.")
            .Produces<RequirementOverview>().ProducesProblem(400).ProducesProblem(404));
        group.MapGet("/pages/{pageId:guid}/definitions", async (Guid pageId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ProjectorCatalog>(new GetPageDefinitions(pageId), ct)))
            .WithName(prefix + "GetProjectorPageDefinitions")
            .WithSummary("The requirement definitions of one page (a deployment or a preset) as the owner reads it: its top-level blocks with a key, with contentJson, " +
                "bundle and moduleKey of the heading above; warnings name keys repeated on the page.")
            .Produces<ProjectorCatalog>().ProducesProblem(404);
        spaceRead(group.MapGet("/spaces/{spaceId:guid}/presets", async (Guid spaceId, bool? details, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<ProjectorPreset>>(new GetProjectorPresets(spaceId, details == true), ct)))
            .WithName(prefix + "GetProjectorPresets")
            .WithSummary("The deployment presets of the space: its page templates the owner may list that hold requirement blocks, with their count. " +
                "With details=true also each preset's first paragraph, products, number of modules and the blocks whose text differs from the catalog " +
                "(reads every preset). A page created from one starts its requirements reset.")
            .Produces<IReadOnlyList<ProjectorPreset>>().ProducesProblem(404));
        spaceRead(group.MapGet("/spaces/{spaceId:guid}/waiting", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<WaitingList>(new GetProjectorWaiting(spaceId), ct)))
            .WithName(prefix + "GetProjectorWaiting").WithSummary("The space's waiting_for_input requirements grouped by party, the longest waiting first.")
            .Produces<WaitingList>().ProducesProblem(404));
        var setCatalog = group.MapPut("/pages/{pageId:guid}/catalog", async (Guid pageId, SetCatalogPageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<CatalogPageState>(new SetCatalogPage(pageId, request.IsCatalog), ct)))
            .WithName(prefix + "SetProjectorCatalogPage")
            .WithSummary("Mark a page as a catalog of its space, or unmark it; takes effect at once. Needs edit rights to the page and the Writer role in its space.")
            .Produces<CatalogPageState>().ProducesProblem(403).ProducesProblem(404);
        var createDeployment = group.MapPost("/pages/{pageId:guid}/deployments", async (Guid pageId, CreateDeploymentRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                var page = await bus.InvokeAsync<PageDto>(new CreateDeploymentFromPage(pageId, request.SpaceId, request.ParentId, request.Title), ct);
                return Results.Created($"/api/pages/{page.Id}", page);
            })
            .WithName(prefix + "CreateProjectorDeployment")
            .WithSummary("Create an unpublished page as a copy of this one: new block ids, every requirement's state, date, party and where reset; keys, directions, requires, notes and attachments kept; tables outside the blocks keep their headers and first column, the section Kontakty only its heading. " +
                "Into another space, or from a page with its own access (which the copy keeps), it needs edit rights to the source.")
            .Produces<PageDto>(201).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);
        return (setCatalog, createDeployment);
    }

    private static void MapVersionOne(RouteGroupBuilder api)
    {
        var (setCatalog, createDeployment) = MapShared(api, "", _ => { });
        Write(setCatalog, ApiOperations.ProjectorCatalogSet);
        Write(createDeployment, ApiOperations.ProjectorDeploymentCreate);
        // GET: a read must not look like a mutation to the /api/v1 policy checks.
        api.MapGet("/spaces/{spaceId:guid}/resolve", async (Guid spaceId, string[] keys, string[]? presentKeys, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementResolution>(new ResolveProjectorRequirements(spaceId, keys, presentKeys ?? []), ct)))
            .WithName("ResolveProjectorRequirements")
            .WithSummary("What inserting the catalog keys (repeat keys=…) would add with their dependencies, in order and with the reason; presentKeys are skipped. A cycle is 400 cyclic_dependency.")
            .Produces<RequirementResolution>().ProducesProblem(400).ProducesProblem(404);
        Write(api.MapPost("/pages/{pageId:guid}/requirements", async (Guid pageId, InsertRequirementsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementInsertResult>(new InsertRequirements(pageId, request.Keys, request.Position ?? new RequirementPosition(),
                    request.ExpectedVersion, request.ModuleKeys), ct)))
            .WithName("InsertRequirements")
            .WithSummary("Insert catalog requirements (at most 100 keys) with the dependencies the page lacks into the working revision at one position (afterBlockId, intoBlockId or headingId; the end without one). " +
                "inserted names each new block and why; warnings name keys already on the page, not applicable prerequisites and unknown keys.")
            .Produces<RequirementInsertResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementInsert);
        Write(api.MapPost("/pages/{pageId:guid}/requirements/new", async (Guid pageId, CreateRequirementRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementInsertResult>(new CreateRequirement(pageId, request.Fields ?? new RequirementChange(),
                    request.Position ?? new RequirementPosition(), request.ExpectedVersion), ct)))
            .WithName("CreateRequirement")
            .WithSummary("Insert one new requirement block into the working revision at one position (afterBlockId, intoBlockId or headingId; the end without one): " +
                "empty, or with fields as the PATCH of one block takes them. inserted names its blockId and the key it got (the slug of its title when none is given).")
            .Produces<RequirementInsertResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementCreate);
        Write(api.MapPost("/pages/{pageId:guid}/requirements/from-deployment", async (Guid pageId, InsertFromDeploymentRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementInsertResult>(new InsertFromDeployment(pageId, request.SourcePageId, request.Keys,
                    request.Position ?? new RequirementPosition(), request.ExpectedVersion), ct)))
            .WithName("InsertRequirementsFromDeployment")
            .WithSummary("Insert requirement blocks of another page (a deployment or a preset, as the owner reads it) by key (all of its keyed top-level blocks without keys), " +
                "with the dependencies the page lacks, reset as copies (new ids, state planned, date, party and where empty). " +
                "A source in another space or with its own access needs edit rights to it.")
            .Produces<RequirementInsertResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementInsertFromDeployment);
        Write(api.MapPatch("/pages/{pageId:guid}/requirements/{blockId}", async (Guid pageId, string blockId, UpdateRequirementRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementWriteResult>(new UpdateRequirement(pageId, blockId, request.Fields, request.ExpectedVersion), ct)))
            .WithName("UpdateRequirement")
            .WithSummary("Change a block's attributes in the working revision (null fields stay); done without a date gets today's date (Europe/Prague). " +
                "A new key moves the requires of the page's other blocks with it; warnings say which.")
            .Produces<RequirementWriteResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementUpdate);
        Write(api.MapPatch("/pages/{pageId:guid}/requirements", async (Guid pageId, UpdateRequirementsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementWriteResult>(new UpdateRequirements(pageId, request.Updates ?? [], request.ExpectedVersion), ct)))
            .WithName("UpdateRequirements")
            .WithSummary("Change several blocks ({blockId, change}, 1–200) in one save of the working revision, in order: one new version, all or none; each change as the single PATCH.")
            .Produces<RequirementWriteResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementUpdateBatch);
        Write(api.MapPost("/pages/{pageId:guid}/requirements/{blockId}/move", async (Guid pageId, string blockId, MoveRequirementRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementWriteResult>(new MoveRequirement(pageId, blockId, request.Position, request.ExpectedVersion), ct)))
            .WithName("MoveRequirement")
            .WithSummary("Move a block with its content and nested blocks in the working revision.")
            .Produces<RequirementWriteResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementMove);
        Write(api.MapDelete("/pages/{pageId:guid}/requirements/{blockId}", async (Guid pageId, string blockId, int expectedVersion, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RequirementWriteResult>(new RemoveRequirement(pageId, blockId, expectedVersion), ct)))
            .WithName("RemoveRequirement")
            .WithSummary("Remove a block with its content and nested blocks from the working revision (?expectedVersion=).")
            .Produces<RequirementWriteResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409), ApiOperations.RequirementRemove);
    }

    /// <summary>The write scope, the audit row of every attempt and the per-token bound of page-sized writes.</summary>
    private static void Write(RouteHandlerBuilder route, string operation)
    {
        // A write of a page's requirement blocks reaches an open editor as a collaborator's update (3.6-H); the catalog and new
        // deployments (MapShared) write no existing page's content.
        if (operation is not (ApiOperations.ProjectorCatalogSet or ApiOperations.ProjectorDeploymentCreate))
        {
            route.LiveWrite();
        }
        route.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(operation).WithMetadata(PersonalApiConcurrency.MarkdownWrites);
    }
}
