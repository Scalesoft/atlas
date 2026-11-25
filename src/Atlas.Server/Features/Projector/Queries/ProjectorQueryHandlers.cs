using System.Globalization;
using System.Text.Json.Nodes;
using NHibernate;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Queries;

public sealed class GetProjectorCatalogHandler(ProjectorCatalogReader catalog)
{
    public async Task<ProjectorCatalog> Handle(GetProjectorCatalog query, CancellationToken ct)
    {
        var keys = query.Keys is { } wanted ? RequirementAttributes.RequireKeys(wanted, "keys", ResolveProjectorRequirementsHandler.MaxPageKeys, asStored: true).ToHashSet() : null;
        var (definitions, warnings) = RequirementResolver.Distinct(await catalog.ReadAsync(query.SpaceId, ct));
        var shown = definitions.Where(x => keys?.Contains(x.Key) != false).Select(x => query.Bodies ? x : x with { ContentJson = null }).ToArray();
        return new ProjectorCatalog(shown, warnings);
    }
}

/// <summary>Present and not applicable keys are what the page holds, so they may run to its size; the wanted ones are a choice.</summary>
public sealed class ResolveProjectorRequirementsHandler(ProjectorCatalogReader catalog)
{
    public const int MaxPageKeys = 5_000;

    public async Task<RequirementResolution> Handle(ResolveProjectorRequirements query, CancellationToken ct)
    {
        var wanted = RequirementAttributes.RequireKeys(query.Keys, "keys", InsertRequirementsHandler.MaxKeys);
        var present = RequirementAttributes.RequireKeys(query.PresentKeys ?? [], "presentKeys", MaxPageKeys, asStored: true);
        var notApplicable = RequirementAttributes.RequireKeys(query.NotApplicableKeys ?? [], "notApplicableKeys", MaxPageKeys, asStored: true);
        return RequirementResolver.Resolve(await catalog.ReadAsync(query.SpaceId, ct), wanted, present, notApplicable);
    }
}

/// <summary>Through PageGate as any page read (404 for a page the actor may not read); the blocks of the revision PageReader would show.</summary>
public sealed class GetPageRequirementsHandler(PageGate gate, RequirementIndex index, ISession session, ProjectorOpenings openings,
    CrossPageRequirements cross)
{
    public async Task<PageRequirements> Handle(GetPageRequirements query, CancellationToken ct)
    {
        var page = (await gate.ReadAsync(query.PageId, AccessOperation.ReadContent, ct)).Require();
        if (page.Page.IsTrashed)
        {
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        var working = await ProjectorPublishedReads.WorkingAsync(session, page, ct);
        var revision = working
            ? await session.QueryOver<PageRevision>().Where(x => x.PageId == query.PageId && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            : page.Page.PublishedRevisionId is { } published ? await session.GetAsync<PageRevision>(published, ct) : null;
        revision = revision ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        // The blocks, their sections, own state word and service (plan 3.4) from the revision itself (C4-9), not the index: by block id,
        // unique on a saved page.
        var rich = JsonNode.Parse(revision.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
        var sections = RequirementIndex.Sections(rich["document"]!);
        var rows = RequirementTree.Read(rich, page.Page.Id);
        // Prerequisites of other pages as this reader reads them (plan 3.1 §2.3 point 5); one they may not read waits as unknown.
        var remote = await cross.PrerequisitesAsync(page.Actor, rows.SelectMany(block => block.Requires), ct);
        var changes = await index.LastChangesAsync(page.Page.Id, revision.Version, working, rows, ct);
        var blocks = RequirementSummary.WithWaitingOn(rows, remote)
            .Select(block => sections.TryGetValue(block.BlockId, out var found)
                ? block with
                {
                    Section = found.Section, StateLabel = RequirementAttributes.Text(found.Block["attrs"], "stateLabel"), System = RequirementAttributes.Text(found.Block["attrs"], "system"),
                    Changed = changes.GetValueOrDefault(block.BlockId)
                }
                : block with { Section = [], Changed = changes.GetValueOrDefault(block.BlockId) }).ToArray();
        if (query.RecordOpening && openings.Records)
        {
            await openings.RecordAsync(page.Actor.Id, [(page.Page.Id, revision.Id)], ct);
        }
        return new PageRequirements(page.Page.Id, page.Page.Version, working, blocks, RequirementSummary.Of(blocks));
    }
}

public sealed class GetProjectorOverviewHandler(SpaceRequirementRows rows, RequirementIndex index)
{
    public async Task<RequirementOverview> Handle(GetProjectorOverview query, CancellationToken ct)
    {
        // The index also filters "waiting" for the waiting list; the overview offers only its own three (review S3-2).
        if (query.Filter is not ("missing" or "inbound-missing" or "all"))
        {
            throw new ApiException(400, "Filtr přehledu je missing, inbound-missing nebo all.", "invalid_filter");
        }
        // Each cell with its section (C4-9), so the matrix groups as the page does.
        return RequirementOverviewBuilder.Build(await index.WithSectionsAsync(await rows.ReadAsync(query.SpaceId, query.Filter, ct), ct));
    }
}

public sealed class SearchSpaceRequirementsHandler(SpaceRequirementRows rows)
{
    public async Task<IReadOnlyList<RequirementRow>> Handle(SearchSpaceRequirements query, CancellationToken ct)
    {
        if (query.Q is { Length: > 200 })
        {
            throw RequirementAttributes.InvalidArgument("q", "q: nejvýše 200 znaků.");
        }
        return await rows.ReadAsync(query.SpaceId, "all", ct, SearchText.LookupWords(query.Q), SearchSpaceRequirements.Limit, query.ExcludePageId);
    }
}

public sealed class GetProjectorWaitingHandler(SpaceRequirementRows rows)
{
    public async Task<WaitingList> Handle(GetProjectorWaiting query, CancellationToken ct)
        => RequirementWaitingBuilder.Build(await rows.ReadAsync(query.SpaceId, "waiting", ct));
}

/// <summary>A page read as PageReader reads it (404 for a page the actor may not read or one in the trash; password references the
/// actor may not see removed; an opening), a normal page or a space template alike.</summary>
public sealed class GetPageDefinitionsHandler(PageReader reader)
{
    public async Task<ProjectorCatalog> Handle(GetPageDefinitions query, CancellationToken ct)
    {
        var (definitions, warnings) = RequirementResolver.Distinct((await ReadAsync(reader, query.PageId, ct)).Definitions);
        return new ProjectorCatalog(definitions, warnings);
    }

    /// <summary>The page as the actor reads it and every definition of it, repeated keys included.</summary>
    internal static async Task<(PageDto Page, IReadOnlyList<CatalogDefinition> Definitions)> ReadAsync(PageReader reader, Guid pageId, CancellationToken ct)
    {
        var page = await reader.ReadAsync(pageId, ct, recordOpening: true);
        return (page, RequirementTree.Definitions(page.Id, JsonNode.Parse(page.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject()));
    }
}

/// <summary>The templates as the template list gives them (its listing rule and order), those with requirement blocks, counted from the
/// index (it holds templates too) in the revision the list shows the actor: the working one to an editor and to a reader of a live
/// template (3.6-I Živě), the publication otherwise.</summary>
/// <remarks>The settings page "Sady a katalog" also needs what a template holds: its first paragraph, the products and the number of
/// modules of its definitions, and the blocks whose text differs from the space catalog's definition of the same key (a key the
/// catalog lacks is no difference).</remarks>
public sealed class GetProjectorPresetsHandler(ListPageTemplatesHandler templates, IContentAccess access, ISession session, PageReader reader,
    ProjectorCatalogReader catalog)
{
    public async Task<IReadOnlyList<ProjectorPreset>> Handle(GetProjectorPresets query, CancellationToken ct)
    {
        var listed = await templates.Handle(new ListPageTemplates(query.SpaceId), ct);
        if (listed.Count == 0)
        {
            return [];
        }
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await ProjectorIndex.EnsureFreshAsync(session, listed.Select(x => x.Id).ToList(), ct);
        var counts = (await session.CreateSQLQuery("""
                SELECT i."PageId",count(*) AS "Requirements" FROM "ProjectorRequirementIndex" i
                JOIN "EffectivePageAccess" e ON e."PageId"=i."PageId" AND e."UserId"=:actor
                JOIN "Page" p ON p."Id"=i."PageId" JOIN "Space" s ON s."Id"=p."SpaceId"
                WHERE i."PageId"=ANY(CAST(string_to_array(:ids,',') AS uuid[])) AND CASE WHEN NOT :published AND (e."Role">=2
                  OR p."PublishedRevisionId" IS NOT NULL AND coalesce(p."PublicationMode",s."PublicationMode")='Live') THEN i."IsWorking" ELSE i."IsPublished" END
                GROUP BY i."PageId"
                """)
            .AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Requirements", NHibernateUtil.Int64)
            .SetParameter("actor", actor.Id).SetParameter("ids", string.Join(",", listed.Select(x => x.Id))).SetParameter("published", ProjectorPublishedReads.Active)
            .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (int)(long)row[1]);
        var shown = listed.Where(x => counts.ContainsKey(x.Id)).ToArray();
        if (shown.Length == 0 || !query.Details)
        {
            return shown.Select(x => new ProjectorPreset(x.Id, x.Title, counts[x.Id])).ToArray();
        }
        // shortcut: reads every preset's content per call; fine for a handful of templates, cache or index it when spaces hold dozens.
        var (known, _) = RequirementResolver.Distinct(await catalog.ReadAsync(query.SpaceId, ct));
        var catalogText = known.Where(x => x.ContentJson is not null).ToDictionary(x => x.Key, x => RequirementTree.ContentMarkdown(x.ContentJson!));
        var presets = new List<ProjectorPreset>();
        foreach (var x in shown)
        {
            var page = await reader.ReadAsync(x.Id, ProjectorPublishedReads.Active ? PageReadVersion.Published : PageReadVersion.Default, false, ct);
            var rich = JsonNode.Parse(page.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
            var definitions = RequirementTree.Definitions(x.Id, rich);
            var differs = definitions.Where(d => d.ContentJson is not null && catalogText.TryGetValue(d.Key, out var text) && text != RequirementTree.ContentMarkdown(d.ContentJson))
                .Select(d => d.Title).ToArray();
            var products = definitions.Select(d => d.Product.Length > 0 ? d.Product : d.Category == RequirementTree.Core ? "" : d.Category)
                .Where(name => name.Length > 0).Distinct().ToArray();
            presets.Add(new ProjectorPreset(x.Id, x.Title, counts[x.Id], RequirementTree.FirstParagraph(rich), products,
                definitions.Select(d => d.ModuleKey).Distinct().Count(), differs));
        }
        return presets;
    }
}

/// <summary>The overview's rows of every block (<see cref="SpaceRequirementRows"/>: the space's 404, openings), the filters checked
/// before anything is read; a module is a catalog heading's moduleKey, its keys those of its definitions. The party matches its text or,
/// for a party that is a contact the actor may read, the contact's organization (owner 2. 11. 2026: „DODAVATEL“ finds Jan Novák of DODAVATEL),
/// both as lookups compare (case and diacritics aside).</summary>
public sealed class QueryRequirementsHandler(SpaceRequirementRows rows, ProjectorCatalogReader catalog, IContentAccess access, ContactDirectory contacts)
{
    public const int MaxLimit = 200;

    public async Task<RequirementQueryPage> Handle(QueryRequirements query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            throw RequirementAttributes.InvalidArgument("limit", $"limit: zadejte 1 až {MaxLimit} (výchozí 50).");
        }
        if (query.Offset < 0)
        {
            throw RequirementAttributes.InvalidArgument("offset", "offset: zadejte 0 nebo víc (nextOffset z předchozí stránky).");
        }
        var states = query.States is { Count: > 0 } wanted ? wanted.Select(x => x?.Trim() ?? "").ToHashSet(StringComparer.Ordinal) : null;
        if (states?.FirstOrDefault(x => !RequirementAttributes.States.Contains(x)) is { } unknown)
        {
            throw RequirementAttributes.InvalidArgument("states", $"states: neznámý stav „{unknown}“; použijte {string.Join(", ", RequirementAttributes.States)}.");
        }
        var from = Day(query.DateFrom, "dateFrom");
        var to = Day(query.DateTo, "dateTo");
        if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        {
            throw RequirementAttributes.InvalidArgument("dateFrom", $"dateFrom: {from} je po dateTo {to}; prohoďte je.");
        }
        var party = ContactFields.Fold(query.Party ?? "");
        var contact = query.PartyContactId?.ToString("D");
        HashSet<string>? keys = null;
        if (query.Module?.Trim() is { Length: > 0 } module)
        {
            keys = (await catalog.ReadAsync(query.SpaceId, ct)).Where(x => x.ModuleKey == module).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            if (keys.Count == 0)
            {
                throw RequirementAttributes.InvalidArgument("module",
                    $"module: modul „{module}“ v katalogu prostoru není; klíče modulů (moduleKey) uvádí katalog prostoru (projector_get_catalog).");
            }
        }
        // shortcut: filters in memory over every block of the space's readable deployment pages (and each of those pages is an opening);
        // move them into RequirementIndex.SpaceAsync as fixed fragments with parameters when a space holds many thousands of blocks.
        var every = await rows.ReadAsync(query.SpaceId, "every", ct, SearchText.LookupWords(query.Text));
        var matched = every.Where(row =>
        {
            var block = row.Requirement;
            return (states is null || states.Contains(block.State))
                && (contact is null || string.Equals(block.PartyContactId, contact, StringComparison.OrdinalIgnoreCase))
                && (from is null && to is null || block.Date.Length > 0 && (from is null || string.CompareOrdinal(block.Date, from) >= 0)
                    && (to is null || string.CompareOrdinal(block.Date, to) <= 0))
                && (keys is null || keys.Contains(block.Key));
        }).ToArray();
        if (party.Length > 0)
        {
            // The party last: its text folded once per distinct party, the contacts read only for the rows its text did not match.
            var named = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Named(RequirementRow row) => named.TryGetValue(row.Requirement.Party, out var hit)
                ? hit
                : named[row.Requirement.Party] = ContactFields.Fold(row.Requirement.Party).Contains(party, StringComparison.Ordinal);
            var organizations = await OrganizationsAsync(query.SpaceId, matched.Where(row => !Named(row)).ToArray(), ct);
            matched = matched.Where(row => Named(row) || organizations.GetValueOrDefault(row.Requirement.PartyContactId)?.Contains(party, StringComparison.Ordinal) == true)
                .ToArray();
        }
        var next = query.Offset + query.Limit;
        return new RequirementQueryPage(matched.Skip(query.Offset).Take(query.Limit).ToArray(), matched.Length, next < matched.Length ? next : null);
    }

    /// <summary>The folded organization of each party contact of <paramref name="found"/> the actor may read (merges followed), by contact id.</summary>
    private async Task<Dictionary<string, string>> OrganizationsAsync(Guid spaceId, IReadOnlyList<RequirementRow> found, CancellationToken ct)
    {
        var ids = found.Select(row => Guid.TryParse(row.Requirement.PartyContactId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        var live = await contacts.LiveAsync(await access.LockActorAsync(spaceId, ct), ids, ct);
        return live.Where(x => x.Value.Contact?["organization"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
            .ToDictionary(x => x.Key.ToString("D"), x => ContactFields.Fold(ContactFields.InlinePlainText(x.Value.Contact!["organization"]!.GetValue<string>())),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A date filter as stored dates compare (YYYY-MM-DD, ordinal); null when not given.</summary>
    private static string? Day(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw RequirementAttributes.InvalidArgument(field, $"{field}: datum ve tvaru RRRR-MM-DD, např. 2026-10-29.");
    }
}

/// <summary>A page read through PageGate (404 for a page the actor may not read or one in the trash); the history in the revision the
/// reader is shown (an editor's working one): the block's dated note lines and the state changes of the versions up to it.</summary>
public sealed class GetRequirementHistoryHandler(PageGate gate, RequirementIndex index, ISession session)
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    public async Task<RequirementHistory> Handle(GetRequirementHistory query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > GetRequirementHistory.MaxLimit)
        {
            throw RequirementAttributes.InvalidArgument("limit", $"limit: zadejte 1 až {GetRequirementHistory.MaxLimit} (výchozí 50).");
        }
        var page = await ProjectorPages.ReadAsync(gate, query.PageId, ct);
        var blockId = ProjectorPages.BlockId(query.BlockId);
        var revision = await ProjectorPublishedReads.WorkingAsync(session, page, ct)
            ? await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            : page.Page.PublishedRevisionId is { } published ? await session.GetAsync<PageRevision>(published, ct) : null;
        if (revision is null)
        {
            return new RequirementHistory(page.Page.Id, blockId, []);
        }
        var rich = JsonNode.Parse(revision.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();
        var block = RequirementTree.Blocks(rich).FirstOrDefault(x => RequirementAttributes.Text(x["attrs"], "id") == blockId);
        var notes = (block is null ? [] : RequirementTree.History(block))
            .Select(x => new RequirementHistoryEntry("note", x.Date, x.Time) { Text = x.Text });
        // At most the limit of state changes: the newest by version are the newest by time, and no more of them can be shown.
        var states = (await index.StateChangesAsync(page.Page.Id, blockId, revision.Version, ct, query.Limit)).Select(x =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.ChangedAt, DateTimeKind.Utc), Prague);
            return new RequirementHistoryEntry("state", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture))
            {
                From = x.From, To = x.To, ChangedById = x.AuthorId, ChangedBy = x.ChangedBy, ChangedAt = x.ChangedAt
            };
        });
        // Newest first; the notes keep the order written within a minute (OrderBy is stable), before a state change of the same minute.
        var entries = notes.Concat(states).OrderByDescending(x => x.Date + " " + (x.Time ?? ""), StringComparer.Ordinal).ToList();
        return new RequirementHistory(page.Page.Id, blockId, query.Limit is { } limit ? entries.Take(limit).ToArray() : entries);
    }
}

public sealed class GetRequirementPrerequisitesHandler(IContentAccess access, CrossPageRequirements cross)
{
    public async Task<IReadOnlyList<RequirementPrerequisite>> Handle(GetRequirementPrerequisites query, CancellationToken ct)
    {
        var references = query.References ?? [];
        if (references.Count > GetRequirementPrerequisites.MaxReferences || references.Any(x => x is null || x.Length > 200 || RequirementAttributes.CrossPage(x) is null))
        {
            throw RequirementAttributes.InvalidArgument("refs",
                $"refs: nejvýše {GetRequirementPrerequisites.MaxReferences} odkazů na požadavky jiných stránek, každý ve tvaru klíč@pageId nebo pageId#headingId.");
        }
        var found = await cross.PrerequisitesAsync(await access.LockActorAsync(null, ct), references, ct);
        return references.Distinct(StringComparer.Ordinal).Select(x => found[x]).ToArray();
    }
}

/// <summary>Through PageGate (404 for a page the actor may not read); the block's key in the revision the reader is shown.</summary>
public sealed class GetRequirementDependentsHandler(PageGate gate, RequirementIndex index, CrossPageRequirements cross, ISession session)
{
    public async Task<RequirementDependents> Handle(GetRequirementDependents query, CancellationToken ct)
    {
        var page = await ProjectorPages.ReadAsync(gate, query.PageId, ct);
        // Every page's index (DependentsAsync reads them all), the page's own among them, in one pass in page order: refreshing the page
        // first and the rest after let two readers each hold a page the other's pass then waited for (40P01).
        await ProjectorIndex.EnsureFreshAsync(session, null, ct);
        var blockId = ProjectorPages.BlockId(query.BlockId);
        var block = (await index.PageAsync(page.Page.Id, await ProjectorPublishedReads.WorkingAsync(session, page, ct), ct)).FirstOrDefault(x => x.BlockId == blockId)
            ?? throw new ApiException(404, "Požadavek na stránce není; načtěte požadavky stránky znovu.", "requirement_not_found");
        var rows = block.Key.Length == 0 ? [] : await cross.DependentsAsync(page.Actor, page.Page.Id, block.Key, ct);
        return new RequirementDependents(page.Page.Id, block.Key, rows);
    }
}

public sealed class CheckRequirementCycleHandler(PageGate gate, RequirementIndex index, CrossPageRequirements cross, ISession session)
{
    public async Task<RequirementCycle> Handle(CheckRequirementCycle query, CancellationToken ct)
    {
        var page = await ProjectorPages.ReadAsync(gate, query.PageId, ct);
        var key = query.Key?.Trim() ?? "";
        var requires = RequirementAttributes.ParseRequires(string.Join(",", query.Requires ?? []));
        if (key.Length == 0 || requires.Count > InsertRequirementsHandler.MaxKeys)
        {
            throw RequirementAttributes.InvalidArgument("key", $"key: klíč požadavku stránky a nejvýše {InsertRequirementsHandler.MaxKeys} závislostí.");
        }
        var local = (await index.PageAsync(page.Page.Id, await ProjectorPublishedReads.WorkingAsync(session, page, ct), ct)).Where(x => x.Key.Length > 0).GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Requires, StringComparer.Ordinal);
        local[key] = requires;
        return new RequirementCycle(await cross.CycleAsync(page.Actor, page.Page.Id, key, requires, local, ct));
    }
}

/// <summary>The page reads of the block queries: through PageGate as any page read, a trashed page a missing one.</summary>
internal static class ProjectorPages
{
    public static async Task<LockedPage> ReadAsync(PageGate gate, Guid pageId, CancellationToken ct)
    {
        var page = (await gate.ReadAsync(pageId, AccessOperation.ReadContent, ct)).Require();
        return page.Page.IsTrashed ? throw new ApiException(404, "Stránka nebyla nalezena.") : page;
    }

    public static string BlockId(string? blockId)
        // A requirement heading's id runs to 128 characters (shape 2026-11-04 §1).
        => string.IsNullOrWhiteSpace(blockId) || blockId.Length > 128 ? throw RequirementAttributes.InvalidArgument("blockId", "blockId je povinné.") : blockId.Trim();
}

/// <summary>A space read as the page tree is one: 404 for a space the actor has neither a role in nor a listed page of; an unknown
/// filter is refused before any statement; every page returned is an opening.</summary>
public sealed class SpaceRequirementRows(IContentAccess access, PageListing listing, RequirementIndex index, ProjectorOpenings openings)
{
    public async Task<IReadOnlyList<RequirementRow>> ReadAsync(Guid spaceId, string filter, CancellationToken ct, IReadOnlyList<string>? words = null,
        int limit = int.MaxValue, Guid? excludePageId = null)
    {
        RequirementIndex.Filter(filter);
        var actor = await access.LockActorAsync(spaceId, ct);
        await listing.RequireSpaceAsync(actor, spaceId, ct);
        var rows = await index.SpaceAsync(actor, spaceId, filter, ct, words, limit, excludePageId);
        await openings.RecordAsync(actor.Id, rows.Select(row => (row.PageId, row.RevisionId)), ct);
        return rows;
    }
}
