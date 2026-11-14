using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Commands;

/// <summary>The catalog is the space's: an editor of the page who is also a Writer of the space (or more) marks it, not a guest with a
/// page grant. Each change is audited in ProjectorCatalogEvent; a mark that is already so changes nothing.</summary>
public sealed class SetCatalogPageHandler(ISession session, PageGate gate, IContentAccess access, McpOrganizationGuard mcp, IRequestChannel channel)
{
    public async Task<CatalogPageState> Handle(SetCatalogPage command, CancellationToken ct)
    {
        var edited = await PageEditing.OpenAsync(gate, mcp, channel, command.PageId, ct);
        try
        {
            await access.RequireSpaceAsync(edited.Space.SpaceId, SpaceRole.Writer, ct);
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            // The page is readable to the actor, so the space is no secret: the answer is the missing right.
            throw new ApiException(403, "Katalog prostoru označuje jen zapisovatel nebo správce prostoru.");
        }
        var mark = await session.GetAsync<ProjectorCatalogPage>(command.PageId, ct);
        if (command.IsCatalog == (mark is not null))
        {
            return new CatalogPageState(command.PageId, command.IsCatalog);
        }
        if (mark is null)
        {
            await session.SaveAsync(new ProjectorCatalogPage { PageId = command.PageId, MarkedById = edited.Actor.Id, MarkedAt = DateTime.UtcNow }, ct);
        }
        else
        {
            await session.DeleteAsync(mark, ct);
        }
        await session.CreateSQLQuery("""
                INSERT INTO "ProjectorCatalogEvent"("PageId","ActorId","IsCatalog","Channel","ApiTokenId") VALUES (:page,:actor,:catalog,:channel,:token)
                """).SetParameter("page", command.PageId).SetParameter("actor", edited.Actor.Id).SetParameter("catalog", command.IsCatalog)
            .SetParameter("channel", channel.Current.Code).SetParameter("token", channel.Current.ApiTokenId, NHibernate.NHibernateUtil.Guid)
            .ExecuteUpdateAsync(ct);
        return new CatalogPageState(command.PageId, command.IsCatalog);
    }
}

/// <summary>
/// Through the shared page create, under its locks, so the copy is any other new page: D12 parent rights, password references the
/// actor may use, the creation channel, the source's attachments copied. Reading the source is an opening of it. A copy that leaves the
/// source's audience (into another space, or of a page with its own access) needs edit rights on the source. A copy of a page with its
/// own access keeps own access (review A2-1) through <see cref="PageGrantCopier"/>; what it could not carry is in the answer's warnings.
/// </summary>
public sealed class CreateDeploymentFromPageHandler(ISession session, IAtlasLocks locks, CreatePageHandler create, PageReader reader, PageGate gate,
    McpOrganizationGuard mcp, IRequestChannel channel, PageGrantCopier grants, AttachmentFiles files)
{
    public async Task<PageDto> Handle(CreateDeploymentFromPage command, CancellationToken ct)
    {
        await LockSpacesAsync(command, ct);
        var actor = await create.LockSpaceAsync(command.SpaceId, ct);
        var source = await reader.ReadAsync(command.SourcePageId, ct, recordOpening: true);
        var sourcePage = (await session.GetAsync<Page>(source.Id, ct))!;
        if (sourcePage.SpaceId != command.SpaceId || sourcePage.OwnAccess)
        {
            (await gate.ReadAsync(source.Id, AccessOperation.EditContent, ct))
                .Require("Kopii do jiného prostoru nebo ze stránky s vlastním přístupem založí jen ten, kdo zdrojovou stránku smí upravovat.");
        }
        var rich = JsonNode.Parse(source.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
        RequirementTree.EmptyForCopy(rich);
        RequirementTree.ResetForCopy(rich);
        RequirementTree.Normalize(rich);
        var copy = await create.Handle(new CreatePage(new CreatePageRequest(command.SpaceId, command.ParentId, command.Title,
            rich.ToJsonString(RichDocument.JsonWrite), null), CopyAttachmentsOf: source.Id), ct);
        if (!sourcePage.OwnAccess)
        {
            return copy;
        }
        // The access of the copy may still be refused (S2-4): the attachments the create copied go with it.
        var copies = await session.QueryOver<Attachment>().Where(x => x.PageId == copy.Id).Select(x => x.Id).ListAsync<Guid>(ct);
        return await AttachmentCopies.DiscardOnFailureAsync(files, copies.ToArray(), async () =>
        {
            var warnings = await grants.CopyAsync(actor, sourcePage, (await session.GetAsync<Page>(copy.Id, ct))!, ct);
            return warnings.Count == 0 ? copy : copy with { Warnings = warnings };
        });
    }

    /// <summary>The source's space and the target space FOR UPDATE in ascending order before any other lock, so two copies in opposite
    /// directions do not wait for each other (the create takes the target FOR UPDATE, the read the source FOR SHARE). On MCP with G5.</summary>
    private async Task LockSpacesAsync(CreateDeploymentFromPage command, CancellationToken ct)
    {
        if (channel.Current.IsAiBound)
        {
            await mcp.LockBothSpacesAsync(command.SourcePageId, command.SpaceId, ct);
            return;
        }
        await locks.AcquireContentSharedAsync(ct);
        var sourceSpace = await session.QueryOver<Page>().Where(x => x.Id == command.SourcePageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        // A missing source is left to the read and its 404.
        foreach (var space in new[] { sourceSpace, command.SpaceId }.Where(x => x != Guid.Empty).Distinct().Order())
        {
            await locks.LockSpaceForUpdateAsync(space, ct);
        }
    }
}

/// <summary>One change (<see cref="UpdateRequirement"/>) or several in one save (<see cref="UpdateRequirements"/>), all or none.</summary>
public sealed class UpdateRequirementHandler(RequirementPageWriter writer, RequirementLinks links)
{
    public const int MaxChanges = 200;

    public Task<RequirementWriteResult> Handle(UpdateRequirement command, CancellationToken ct)
        => UpdateAsync(command.PageId, [new RequirementBlockChange(command.BlockId, command.Fields)], command.ExpectedVersion, false, ct);

    public Task<RequirementWriteResult> Handle(UpdateRequirements command, CancellationToken ct)
        => UpdateAsync(command.PageId, command.Updates, command.ExpectedVersion, true, ct);

    private async Task<RequirementWriteResult> UpdateAsync(Guid pageId, IReadOnlyList<RequirementBlockChange>? changes, int expectedVersion, bool batch,
        CancellationToken ct)
    {
        if (changes is null || changes.Count is 0 or > MaxChanges || changes.Any(x => x?.BlockId is null || x.Change is null))
        {
            throw new ApiException(400, "Pošlete 1 až {maxChanges} změn, každou s blockId a poli.", "invalid_argument", args: new Dictionary<string, object?> { ["maxChanges"] = MaxChanges });
        }
        var draft = await writer.OpenAsync(pageId, expectedVersion, ct);
        var today = Today();
        var resolved = new List<RequirementBlockChange>();
        var warnings = new List<string>();
        foreach (var change in changes)
        {
            var (fields, found) = await links.ContactAsync(change.Change, ct);
            resolved.Add(change with { Change = fields });
            warnings.AddRange(found);
        }
        warnings.AddRange(resolved.SelectMany((x, i) => Apply(draft.Rich, pageId, x, today, batch ? i : null)));
        foreach (var change in resolved.Where(x => x.Change.Requires is not null))
        {
            await links.RefuseCycleAsync(draft, change.BlockId, ct);
        }
        warnings.AddRange(RequirementTree.MissingPrerequisites(draft.Rich, resolved.Where(x => x.Change.Requires is not null).Select(x => x.BlockId).ToHashSet(), pageId));
        return new RequirementWriteResult(pageId, await writer.SaveAsync(draft, ct)) { Warnings = warnings };
    }

    /// <summary>One change; in a batch a refused one is named (its number and blockId, and details index and blockId), so the client
    /// knows which to fix.</summary>
    private static IReadOnlyList<string> Apply(JsonObject rich, Guid pageId, RequirementBlockChange change, DateOnly today, int? index)
    {
        try
        {
            return RequirementTree.Update(rich, change.BlockId, change.Change, today, pageId);
        }
        catch (ApiException error) when (error.Status == 400 && index is { } i)
        {
            var details = new Dictionary<string, object?>(error.Extensions ?? new Dictionary<string, object?>()) { ["index"] = i, ["blockId"] = change.BlockId };
            throw new ApiException(400, $"Změna č. {i + 1} (blockId {change.BlockId}): {error.Message}", error.Code, details);
        }
    }

    /// <summary>The date a block marked done gets (spec §2.1), in the wiki's time zone.</summary>
    internal static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Prague"));
}

/// <summary>The page is opened for editing first, so a page the actor may not edit answers the same wherever it is; then the catalog
/// of its space is read.</summary>
public sealed class InsertRequirementsHandler(ProjectorCatalogReader catalog, RequirementPageWriter writer)
{
    public const int MaxKeys = 100;

    public async Task<RequirementInsertResult> Handle(InsertRequirements command, CancellationToken ct)
    {
        var modules = command.ModuleKeys ?? [];
        if (modules.Count > MaxKeys || modules.Any(string.IsNullOrWhiteSpace))
        {
            throw RequirementAttributes.InvalidArgument("moduleKeys", $"moduleKeys: nejvýše {MaxKeys} klíčů modulů (moduleKey z katalogu), žádný prázdný.");
        }
        // Whole modules alone need no keys.
        IReadOnlyList<string> keys = modules.Count > 0 && command.Keys is not { Count: > 0 } ? [] : RequirementAttributes.RequireKeys(command.Keys, "keys", MaxKeys);
        var position = command.Position ?? throw MissingPosition();
        var draft = await writer.OpenAsync(command.PageId, command.ExpectedVersion, ct);
        var definitions = await catalog.ReadAsync(draft.Page.Space.SpaceId, ct);
        if (modules.FirstOrDefault(module => definitions.All(definition => definition.ModuleKey != module)) is { } unknown)
        {
            throw RequirementAttributes.InvalidArgument("moduleKeys", $"moduleKeys: modul „{unknown}“ v katalogu prostoru není; moduleKey vrací projector_get_catalog.");
        }
        keys = [.. keys.Concat(definitions.Where(definition => modules.Contains(definition.ModuleKey)).Select(definition => definition.Key)).Distinct(StringComparer.Ordinal)];
        return await InsertAsync(writer, draft, definitions, keys, position, ct, modules: modules);
    }

    /// <summary><paramref name="keys"/> resolved against <paramref name="definitions"/> and the keys the page holds (present, not
    /// applicable), inserted at <paramref name="position"/> and saved with the attachments of the definitions' pages copied. The blocks
    /// of <paramref name="modules"/> none of whose blocks the page holds yet go last, each module under its catalog heading one level
    /// below the heading they are placed under (plan 3.4 §6); what they need from elsewhere goes before them.</summary>
    internal static async Task<RequirementInsertResult> InsertAsync(RequirementPageWriter writer, RequirementDraft draft, IReadOnlyList<CatalogDefinition> definitions,
        IReadOnlyList<string> keys, RequirementPosition position, CancellationToken ct, string source = RequirementResolver.InCatalog,
        IReadOnlyList<string>? modules = null)
    {
        var blocks = RequirementTree.Blocks(draft.Rich).ToArray();
        var present = blocks.Select(RequirementTree.Key).ToArray();
        var resolution = RequirementResolver.Resolve(definitions, keys, present,
            blocks.Where(block => RequirementAttributes.Text(block["attrs"], "state") == "not_applicable").Select(RequirementTree.Key).ToArray(), source);
        // O5: from the catalog without a place, each block goes to the section its Produkt › Kategorie › Modul names (the editor's placement);
        // a page that has none of those sections keeps the end of the page, whole modules under their heading.
        // „Jádro“ with no product above it stands for no category heading at all, not for a heading to make (RequirementTree.Definitions).
        var paths = resolution.Items.Select(item => new[] { item.Definition.Product,
                item.Definition.Product.Length == 0 && item.Definition.Category == RequirementTree.Core ? "" : item.Definition.Category, item.Definition.Bundle }
            .Select(name => name.Trim()).Where(name => name.Length > 0).ToArray()).ToArray();
        if (position == new RequirementPosition() && source == RequirementResolver.InCatalog && paths.Any(path => RequirementTree.OnPage(draft.Rich, path)))
        {
            var placed = RequirementTree.InsertByPath(draft.Rich, resolution.Items.Select(item => item.Definition.ContentJson!).ToList(), paths, draft.Page.Page.Id);
            return await SaveAsync(writer, draft, resolution, resolution.Items, placed, ct);
        }
        var whole = (modules ?? []).Where(module => !definitions.Any(definition => definition.ModuleKey == module && present.Contains(definition.Key))).ToHashSet(StringComparer.Ordinal);
        var ordered = resolution.Items.Where(item => !whole.Contains(item.Definition.ModuleKey)).ToList();
        var nodes = ordered.Select(item => item.Definition.ContentJson!).ToList();
        var level = RequirementTree.HeadingLevel(draft.Rich, position);
        foreach (var module in resolution.Items.Where(item => whole.Contains(item.Definition.ModuleKey)).GroupBy(item => item.Definition.ModuleKey))
        {
            nodes.Add(RequirementTree.Heading(module.First().Definition.Bundle, level));
            nodes.AddRange(module.Select(item => item.Definition.ContentJson!));
            ordered.AddRange(module);
        }
        var inserted = RequirementTree.Insert(draft.Rich, nodes, position, draft.Page.Page.Id).Where(node => RequirementAttributes.Text(node, "type") == RequirementAttributes.NodeType);
        return await SaveAsync(writer, draft, resolution, ordered, inserted, ct);
    }

    private static async Task<RequirementInsertResult> SaveAsync(RequirementPageWriter writer, RequirementDraft draft, RequirementResolution resolution,
        IEnumerable<ResolvedRequirement> ordered, IEnumerable<JsonObject> inserted, CancellationToken ct)
    {
        var version = await writer.SaveAsync(draft, ct, resolution.Items.Select(item => item.Definition.PageId).ToHashSet());
        return new RequirementInsertResult(draft.Page.Page.Id, version, ordered.Zip(inserted, (item, block) =>
                new InsertedRequirement(RequirementTree.Key(block), RequirementAttributes.Text(block["attrs"], "id"), item.RequiredBy, item.Reason)).ToArray(),
            [.. resolution.Warnings, .. RequirementTree.MissingPrerequisites(draft.Rich, pageId: draft.Page.Page.Id)]);
    }

    internal static ApiException MissingPosition()
        => RequirementAttributes.InvalidArgument("position", "Zadejte position (afterBlockId, intoBlockId, headingId, nebo prázdné místo pro konec stránky).");
}

/// <summary>An empty block placed as an inserted copy is (a new id, state planned), then changed as <see cref="UpdateRequirement"/>
/// changes one, in one save; the answer names its blockId and the key the save gave it.</summary>
public sealed class CreateRequirementHandler(RequirementPageWriter writer, RequirementLinks links)
{
    public async Task<RequirementInsertResult> Handle(CreateRequirement command, CancellationToken ct)
    {
        var position = command.Position ?? throw InsertRequirementsHandler.MissingPosition();
        var draft = await writer.OpenAsync(command.PageId, command.ExpectedVersion, ct);
        var empty = new JsonObject
        {
            ["type"] = RequirementAttributes.NodeType, ["attrs"] = new JsonObject { ["key"] = "", ["direction"] = "internal", ["requires"] = "" },
            ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" })
        };
        var pageId = draft.Page.Page.Id;
        var block = RequirementTree.Insert(draft.Rich, [empty], position, pageId)[0];
        var blockId = RequirementAttributes.Text(block["attrs"], "id");
        var (fields, found) = await links.ContactAsync(command.Fields ?? new RequirementChange(), ct);
        var page = (JsonArray)draft.Rich["document"]!["content"]!;
        var heading = RequirementHeadingWrites.Find(page, blockId) is not null;
        // A new requirement heading's id is its key, else the slug of its title (as a block's key), not the placeholder it was inserted with.
        var warnings = found.Concat(RequirementTree.Update(draft.Rich, blockId, heading && fields.Key is null ? fields with { Key = "" } : fields,
            UpdateRequirementHandler.Today(), pageId)).ToList();
        if (heading && RequirementHeadingWrites.Find(page, blockId) is { } at)
        {
            ((JsonObject)page[at]!["attrs"]!).Remove("aliases");
            blockId = RequirementHeadings.HeadingId(page[at]);
        }
        await links.RefuseCycleAsync(draft, blockId, ct);
        warnings.AddRange(RequirementTree.MissingPrerequisites(draft.Rich, new HashSet<string> { blockId }, pageId));
        var version = await writer.SaveAsync(draft, ct);
        return new RequirementInsertResult(command.PageId, version,
            [new InsertedRequirement(heading ? blockId : RequirementTree.Key(block), blockId, null, "Nový požadavek")], warnings);
    }
}

/// <summary>
/// <see cref="InsertRequirements"/> with another page as the catalog (plan 3.1 §2.3): its definitions as the actor reads it
/// (<see cref="GetPageDefinitionsHandler.ReadAsync"/>: an opening, password references the actor may not see removed); a source in another
/// space or with its own access only for an editor of it, as for a copy of the whole page. Both spaces are locked FOR UPDATE in ascending
/// order first, as <see cref="CreateDeploymentFromPageHandler"/> locks them, so two insertions between two pages in opposite directions do
/// not wait for each other; on MCP the source passes G5 under that lock (the target in the writer), an excluded one is a missing page.
/// </summary>
public sealed class InsertFromDeploymentHandler(ISession session, RequirementPageWriter writer, PageReader reader, PageGate gate, McpOrganizationGuard mcp,
    IRequestChannel channel)
{
    public async Task<RequirementInsertResult> Handle(InsertFromDeployment command, CancellationToken ct)
    {
        if (command.SourcePageId == command.PageId)
        {
            throw RequirementAttributes.InvalidArgument("sourcePageId",
                "sourcePageId: požadavky se vkládají z jiné stránky, než do které se vkládají; vyberte jiné nasazení nebo předvolbu.");
        }
        var keys = command.Keys is { Count: > 0 } wanted ? RequirementAttributes.RequireKeys(wanted, "keys", InsertRequirementsHandler.MaxKeys) : null;
        var position = command.Position ?? throw InsertRequirementsHandler.MissingPosition();
        await mcp.LockSpacesOfPagesAsync([command.PageId, command.SourcePageId], ct);
        if (channel.Current.IsAiBound)
        {
            await mcp.LockPageSpaceSharedAsync(command.SourcePageId, ct);
        }
        var draft = await writer.OpenAsync(command.PageId, command.ExpectedVersion, ct);
        var (source, definitions) = await GetPageDefinitionsHandler.ReadAsync(reader, command.SourcePageId, ct);
        var sourcePage = (await session.GetAsync<Page>(source.Id, ct))!;
        if (sourcePage.SpaceId != draft.Page.Space.SpaceId || sourcePage.OwnAccess)
        {
            (await gate.ReadAsync(source.Id, AccessOperation.EditContent, ct))
                .Require("Z nasazení v jiném prostoru nebo ze stránky s vlastním přístupem vkládá jen ten, kdo zdrojovou stránku smí upravovat.");
        }
        return await InsertRequirementsHandler.InsertAsync(writer, draft, definitions, keys ?? definitions.Select(x => x.Key).Distinct().ToArray(), position, ct,
            RequirementResolver.OnSourcePage);
    }
}

public sealed class MoveRequirementHandler(RequirementPageWriter writer)
{
    public async Task<RequirementWriteResult> Handle(MoveRequirement command, CancellationToken ct)
    {
        var position = command.Position ?? throw InsertRequirementsHandler.MissingPosition();
        var draft = await writer.OpenAsync(command.PageId, command.ExpectedVersion, ct);
        RequirementTree.Move(draft.Rich, command.BlockId, position);
        return new RequirementWriteResult(command.PageId, await writer.SaveAsync(draft, ct));
    }
}

public sealed class RemoveRequirementHandler(RequirementPageWriter writer)
{
    public async Task<RequirementWriteResult> Handle(RemoveRequirement command, CancellationToken ct)
    {
        var draft = await writer.OpenAsync(command.PageId, command.ExpectedVersion, ct);
        RequirementTree.Remove(draft.Rich, command.BlockId);
        return new RequirementWriteResult(command.PageId, await writer.SaveAsync(draft, ct));
    }
}
