using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Search.Contracts;

using Atlas.Server.Platform.Objects;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// Read tools that answer a whole question in one call, so a small model with a short context does not walk pages one by one: the
/// whole tree of a space, every page mentioning a term, who has access and why. The server iterates up to a hard cap and says when it
/// stopped (truncated + nextCursor). Each runs the web's shared handler (rights, locks) and narrows it by G5. Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed partial class McpOverviewTools
{
    public const int TreeLimit = 500;
    public const int MentionLimit = 200;
    public const int PeopleLimit = 200;
    private const int SearchWindow = 50;
    private const int SearchRounds = MentionLimit / SearchWindow + 2;
    private const int PeopleWindow = 100;
    private const int Budget = McpText.Limit - 2_000;

    private readonly IMessageBus m_bus;
    private readonly McpSearchLimiter m_searchLimiter;
    private readonly ILogger<McpOverviewTools> m_logger;
    private readonly ObjectTypes m_types;

    public McpOverviewTools(IMessageBus bus, McpSearchLimiter searchLimiter, ILogger<McpOverviewTools> logger, ObjectTypes types)
    {
        m_bus = bus;
        m_types = types;
        m_searchLimiter = searchLimiter;
        m_logger = logger;
    }

    [McpServerTool(Name = "atlas_get_space_tree", Title = "Strom prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Celý strom stránek prostoru jedním voláním: pageId, rodič, hloubka, cesta, ikona, barva, tagy. spaceId; nad 500 stránek pokračuj přes cursor.")]
    [Description("Vrátí celý strom stránek prostoru jedním voláním (nejvýše 500 stránek, do hloubky v pořadí webu): pageId, parentId, název, " +
        "hloubka a cesta, ikona s barvou (revisionId pro expectedRevisionId), tagy a zda je stránka publikovaná. Jen stránky, které vlastník tokenu smí číst. Je-li stránek víc, " +
        "výsledek má truncated: true a nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> GetSpaceTreeAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_get_space_tree", async () =>
        {
            var space = McpArguments.RequiredId(spaceId, "spaceId");
            var offset = McpCursor.Offset(cursor, "tree");
            var tree = await m_bus.InvokeAsync<McpSpaceTree>(new GetMcpSpaceTree(space, offset, TreeLimit), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            foreach (var entry in tree.Items)
            {
                var page = entry.Item;
                var line = new StringBuilder();
                line.Append(' ', entry.Depth * 2).Append("- ").Append(McpText.Line(page.Title, 200)).Append(" (pageId: ").Append(page.Id);
                if (page.Decoration is { Icon: { } icon })
                {
                    line.Append(", ikona ").Append(icon).Append('/').Append(page.Decoration.Color);
                }
                if (page.Decoration is { } decoration)
                {
                    // expectedRevisionId of atlas_set_decoration and atlas_apply_labels; a cleared icon keeps its record and revision.
                    line.Append(", revisionId ").Append(decoration.RevisionId);
                }
                if (entry.Tags.Count > 0)
                {
                    line.Append(", tagy: ").Append(string.Join(", ", entry.Tags.Select(tag => McpText.Line(tag.Name, 60))));
                }
                line.Append(page.IsPublished ? "" : ", jen koncept").Append(page.ChildrenByTitle ? ", podstránky řazené abecedně" : "").Append(")\n");
                if (body.Length + line.Length > Budget)
                {
                    break;
                }
                body.Append(line);
                items.Add(new JsonObject
                {
                    ["pageId"] = page.Id, ["parentId"] = page.ParentId, ["title"] = page.Title, ["depth"] = entry.Depth, ["path"] = entry.Path,
                    ["icon"] = page.Decoration?.Icon, ["color"] = page.Decoration?.Color, ["revisionId"] = page.Decoration?.RevisionId,
                    ["tags"] = new JsonArray(entry.Tags.Select(tag => (JsonNode)JsonValue.Create(tag.Name)!).ToArray()), ["isPublished"] = page.IsPublished,
                    ["childrenByTitle"] = page.ChildrenByTitle
                });
            }
            var shown = items.Count;
            var nextCursor = offset + shown < tree.Total ? McpCursor.Create("tree", new JsonObject { ["o"] = offset + shown }) : null;
            var structured = new JsonObject { ["spaceId"] = space, ["treeVersion"] = tree.Version, ["rootByTitle"] = tree.RootByTitle, ["total"] = tree.Total, ["truncated"] = nextCursor is not null, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            var heading = $"Strom prostoru {space}: stránky {(shown == 0 ? 0 : offset + 1)}–{offset + shown} z {tree.Total} " +
                $"(treeVersion {tree.Version.ToString(CultureInfo.InvariantCulture)}). Odsazení značí podstránky.";
            return McpText.Result(McpText.Wrap(heading, shown == 0 ? "Žádné stránky." : body.ToString(),
                nextCursor is null ? null : $"Zkráceno. Další stránky: stejné spaceId a cursor \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_find_mentions", Title = "Kde se zmiňuje", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Úplný seznam publikovaných stránek s přesným výrazem (až 200), na otázky „kde všude“. term, volitelně spaceId, cursor.")]
    [Description("Vypíše jedním voláním všechny publikované stránky, jejichž text nebo název obsahuje zadaný výraz (slova v jakémkoli českém tvaru, ne význam ani " +
        "překlepy; fráze v uvozovkách), nejvýše 200: pageId, cesta, název a krátký úryvek. Jen stránky, které vlastník tokenu smí číst. " +
        "Je-li jich víc, výsledek má truncated: true a nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> FindMentionsAsync(ClaimsPrincipal user,
        [Description("Hledaný výraz (jméno, kód, název systému…).")] string term,
        [Description("Volitelně jen v tomto prostoru (spaceId z atlas_list_spaces).")] string? spaceId = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_find_mentions", async () =>
        {
            var text = term?.Trim() ?? "";
            if (text.Length == 0)
            {
                return McpErrors.InvalidArgument("Zadejte hledaný výraz.");
            }
            var space = McpArguments.OptionalId(spaceId, "spaceId");
            var position = McpCursor.Read(cursor, "mentions");
            var offset = position is null ? 0 : McpCursor.Int(position, "o", 0, 1_000);
            var skip = position is null ? 0 : McpCursor.Int(position, "s", 0, SearchWindow);
            // The atlas_search budget, keyed as there: per token over /mcp, per signed-in user in process. One call spends one search.
            var budget = user.FindFirst(PersonalApiAuthentication.TokenClaim)?.Value
                ?? (user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId ? "user:" + userId : null)
                ?? throw new ApiException(401, "Platný osobní API token je vyžadován.");
            if (m_searchLimiter.TryAcquire(budget) is { } retryAfter)
            {
                return McpErrors.Error(429, "rate_limited", $"Příliš mnoho hledání. Zkuste to znovu za {retryAfter} s.",
                    new JsonObject { ["retryAfter"] = retryAfter });
            }
            // Lexical (no vector, no typo tolerance) over published text; search itself matches Czech word forms (SearchText.Stem). G5 removes hits after each window, so a resume
            // position is the window's offset and the index among its remaining hits.
            var options = new SearchOptions(Content: "published", AiScopeOnly: true);
            var hits = new List<(SearchHit Hit, JsonObject At)>();
            JsonObject? rest = null;
            int? from = offset;
            for (var round = 0; round < SearchRounds && from is { } window && hits.Count < MentionLimit; round++)
            {
                var response = await m_bus.InvokeAsync<SearchResponse>(
                    new Search.Queries.Search(text, space, window, SearchWindow, options) { Fuzzy = false }, ct);
                var index = skip;
                foreach (var hit in response.Items.Skip(skip))
                {
                    var at = new JsonObject { ["o"] = window, ["s"] = index++ };
                    if (hits.Count == MentionLimit)
                    {
                        rest = at;
                        break;
                    }
                    hits.Add((hit, at));
                }
                skip = 0;
                from = response.NextOffset;
            }
            rest ??= from is { } next ? new JsonObject { ["o"] = next, ["s"] = 0 } : null;
            var paths = hits.Count == 0
                ? new Dictionary<Guid, string>()
                : (await m_bus.InvokeAsync<McpPaths>(new GetMcpPaths(hits.Select(x => new McpPathRequest(x.Hit.Id, x.Hit.SpaceName, x.Hit.Title)).ToArray()), ct)).Items;

            var body = new StringBuilder();
            var items = new JsonArray();
            foreach (var (hit, at) in hits)
            {
                var path = paths.GetValueOrDefault(hit.Id) ?? hit.SpaceName + " › " + hit.Title;
                var snippet = McpText.Line(hit.Snippet, 160);
                var entry = $"{items.Count + 1}. {McpText.Line(path, 300)}\n   pageId: {hit.Id}\n   úryvek: {snippet}\n";
                if (body.Length + entry.Length > Budget)
                {
                    rest = at;
                    break;
                }
                body.Append(entry);
                items.Add(new JsonObject { ["pageId"] = hit.Id, ["title"] = hit.Title, ["path"] = path, ["spaceId"] = hit.SpaceId, ["snippet"] = snippet });
            }
            var nextCursor = rest is null ? null : McpCursor.Create("mentions", rest);
            var structured = new JsonObject { ["truncated"] = nextCursor is not null, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            var heading = $"Stránky zmiňující {McpText.Quote(McpText.Line(text, 200))}: {items.Count}" + (nextCursor is null ? "." : " (zkráceno, další přes nextCursor).");
            return McpText.Result(McpText.Wrap(heading, items.Count == 0 ? "Nic nenalezeno." : body.ToString(),
                nextCursor is null ? null : $"Další výsledky: zavolejte atlas_find_mentions se stejným výrazem a cursor: \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_explain_access", Title = "Kdo má přístup a proč", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Kdo má přístup ke stránce či prostoru a proč, jen ke čtení. pageId nebo spaceId, volitelně userId pro jednoho uživatele.")]
    [Description("Vysvětlí, kdo má přístup ke stránce nebo prostoru a proč, jen ke čtení. S pageId: lidé a skupiny s rolí a zdroji přístupu " +
        "(nejvýše 200 lidí); kdo nesmí spravovat přístupy stránky, uvidí jen svůj vlastní přístup, jako na webu. S pageId a userId: " +
        "zda daný uživatel stránku uvidí či upraví a proč (jen pro toho, kdo smí přístupy stránky spravovat). Se spaceId: přiřazení rolí prostoru " +
        "(jen pro správce prostoru). " + McpText.TrustNotice)]
    public Task<CallToolResult> ExplainAccessAsync(
        [Description("pageId stránky (nebo spaceId).")] string? pageId = null,
        [Description("spaceId prostoru (nebo pageId).")] string? spaceId = null,
        [Description("Volitelně s pageId: userId uživatele, jehož přístup vysvětlit.")] string? userId = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_explain_access", async () =>
        {
            var page = McpArguments.OptionalId(pageId, "pageId");
            var space = McpArguments.OptionalId(spaceId, "spaceId");
            var subject = McpArguments.OptionalId(userId, "userId");
            if (page is null == space is null)
            {
                return McpErrors.InvalidArgument("Zadejte buď pageId, nebo spaceId.");
            }
            if (subject is not null && page is null)
            {
                return McpErrors.InvalidArgument("userId jde zadat jen s pageId.");
            }
            var offset = McpCursor.Offset(cursor, "access");
            var scope = await m_bus.InvokeAsync<McpScope>(new CheckMcpScope(page is { } p ? [p] : [], space is { } s ? [s] : []), ct);
            if (space is { } spaceToExplain)
            {
                if (scope.ExcludedSpaces.Contains(spaceToExplain))
                {
                    throw new ApiException(404, McpVisibility.MissingSpace);
                }
                return SpaceAccess(spaceToExplain, await m_bus.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(spaceToExplain), ct), offset);
            }
            var pageToExplain = page!.Value;
            if (!scope.PagesInScope.Contains(pageToExplain))
            {
                throw new ApiException(404, McpVisibility.MissingPage);
            }
            if (subject is { } user)
            {
                return Explanation(pageToExplain, await m_bus.InvokeAsync<PageAccessExplanation>(new ExplainPageAccess(pageToExplain, user), ct));
            }
            return await PageAccessAsync(pageToExplain, offset, ct);
        }, ct);

    private async Task<CallToolResult> PageAccessAsync(Guid pageId, int offset, CancellationToken ct)
    {
        var state = await m_bus.InvokeAsync<PageAccessState>(new GetPageAccess(pageId, offset, PeopleWindow), ct);
        var people = state.Items.ToList();
        var hasMore = state.HasMore;
        while (state.CanManage && hasMore && people.Count < PeopleLimit)
        {
            var next = await m_bus.InvokeAsync<PageAccessState>(new GetPageAccess(pageId, offset + people.Count, PeopleWindow), ct);
            people.AddRange(next.Items);
            hasMore = next.HasMore;
        }
        var body = new StringBuilder();
        body.Append("stránka: ").Append(McpText.Line(state.PageTitle, 200)).Append(" (prostor ").Append(McpText.Line(state.SpaceName, 200)).Append(")\n");
        body.Append("váš přístup: ").Append(Person(state.MyAccess)).Append('\n');
        var structured = new JsonObject
        {
            ["pageId"] = pageId, ["spaceId"] = state.SpaceId, ["canManage"] = state.CanManage, ["myAccess"] = PersonNode(state.MyAccess)
        };
        if (!state.CanManage)
        {
            body.Append("Seznam lidí s přístupem vidí jen ten, kdo smí přístupy této stránky spravovat.\n");
            return McpText.Result(McpText.Wrap($"Přístup ke stránce {pageId}.", body.ToString()), structured);
        }
        body.Append("režim: ").Append(state.Mode == "custom" ? "vlastní přístupy stránky" : "přebírá výchozí přístupy prostoru").Append('\n');
        if (state.Groups is { Count: > 0 } groups)
        {
            body.Append("skupiny:\n");
            foreach (var group in groups)
            {
                body.Append("- ").Append(McpText.Line(group.Name, 120)).Append(" (groupId: ").Append(group.GroupId).Append(", členů ").Append(group.MemberCount)
                    .Append(", role ").Append(group.Role ?? "žádná").Append("): ").Append(Sources(group.Sources)).Append('\n');
            }
        }
        body.Append("lidé:\n");
        var items = new JsonArray();
        foreach (var person in people)
        {
            var line = "- " + Person(person) + "\n";
            if (body.Length + line.Length > Budget)
            {
                break;
            }
            body.Append(line);
            items.Add(PersonNode(person));
        }
        var more = items.Count < people.Count || hasMore;
        var nextCursor = more ? McpCursor.Create("access", new JsonObject { ["o"] = offset + items.Count }) : null;
        structured["mode"] = state.Mode;
        structured["groups"] = new JsonArray((state.Groups ?? []).Select(group => (JsonNode)new JsonObject
        {
            ["groupId"] = group.GroupId, ["name"] = group.Name, ["memberCount"] = group.MemberCount, ["role"] = group.Role, ["sources"] = SourceNodes(group.Sources)
        }).ToArray());
        structured["items"] = items;
        structured["truncated"] = more;
        if (nextCursor is not null)
        {
            structured["nextCursor"] = nextCursor;
        }
        return McpText.Result(McpText.Wrap($"Přístup ke stránce {pageId}: {items.Count} lidí" + (more ? " (zkráceno)." : "."), body.ToString(),
            nextCursor is null ? null : $"Další lidé: stejné pageId a cursor \"{nextCursor}\"."), structured);
    }

    private static CallToolResult Explanation(Guid pageId, PageAccessExplanation explanation)
    {
        var body = new StringBuilder();
        body.Append("uživatel: ").Append(McpText.Line(explanation.DisplayName, 120)).Append(" (userId: ").Append(explanation.UserId).Append(")\n");
        body.Append("vidí stránku: ").Append(explanation.CanView ? "ano" : "ne").Append(", upraví ji: ").Append(explanation.CanEdit ? "ano" : "ne")
            .Append(", role: ").Append(explanation.Role ?? "žádná").Append('\n');
        if (explanation.Denial is { } denial)
        {
            body.Append("důvod odepření: ").Append(DenialLabel(denial)).Append('\n');
        }
        body.Append("zdroje přístupu: ").Append(Sources(explanation.Sources)).Append('\n');
        return McpText.Result(McpText.Wrap($"Přístup uživatele ke stránce {pageId}.", body.ToString()), new JsonObject
        {
            ["pageId"] = pageId, ["userId"] = explanation.UserId, ["displayName"] = explanation.DisplayName, ["canView"] = explanation.CanView,
            ["canEdit"] = explanation.CanEdit, ["role"] = explanation.Role, ["denial"] = explanation.Denial, ["sources"] = SourceNodes(explanation.Sources)
        });
    }

    private static CallToolResult SpaceAccess(Guid spaceId, SpaceAccessSummary summary, int offset)
    {
        var body = new StringBuilder();
        var items = new JsonArray();
        foreach (var grant in summary.Grants.Skip(offset))
        {
            var line = $"- {McpText.Line(grant.Name, 120)} ({(grant.GroupId is { } group ? "skupina, groupId: " + group : "uživatel, userId: " + grant.UserId)}, role {grant.Role}" +
                $"{Validity(grant.StartAt, grant.ExpiresAt)})\n";
            if (items.Count == PeopleLimit || body.Length + line.Length > Budget)
            {
                break;
            }
            body.Append(line);
            items.Add(new JsonObject
            {
                ["userId"] = grant.UserId, ["groupId"] = grant.GroupId, ["name"] = grant.Name, ["role"] = grant.Role, ["startAt"] = grant.StartAt, ["expiresAt"] = grant.ExpiresAt
            });
        }
        var more = offset + items.Count < summary.Grants.Count;
        var nextCursor = more ? McpCursor.Create("access", new JsonObject { ["o"] = offset + items.Count }) : null;
        var structured = new JsonObject { ["spaceId"] = spaceId, ["truncated"] = more, ["items"] = items };
        if (nextCursor is not null)
        {
            structured["nextCursor"] = nextCursor;
        }
        return McpText.Result(McpText.Wrap($"Přiřazení rolí prostoru {spaceId}: {items.Count}." +
                " Správce prostoru vidí všechny stránky; výchozí role čtenář a editor platí pro stránky bez vlastních přístupů.",
            items.Count == 0 ? "Žádná přiřazení." : body.ToString(), nextCursor is null ? null : $"Další přiřazení: stejné spaceId a cursor \"{nextCursor}\"."), structured);
    }

    private static string Person(AccessPerson person)
        => $"{McpText.Line(person.DisplayName, 120)} (userId: {person.UserId}, role {person.Role ?? "žádná"}" +
            $"{(person.CanEdit ? ", upravuje" : person.CanReadPublished ? ", čte" : "")}{(person.IsWikiAdmin ? ", správce wiki" : "")}): {Sources(person.Sources)}";

    private static JsonObject PersonNode(AccessPerson person) => new()
    {
        ["userId"] = person.UserId, ["displayName"] = person.DisplayName, ["role"] = person.Role, ["isWikiAdmin"] = person.IsWikiAdmin,
        ["canReadPublished"] = person.CanReadPublished, ["canReadDraft"] = person.CanReadDraft, ["canEdit"] = person.CanEdit, ["sources"] = SourceNodes(person.Sources)
    };

    private static string Sources(IReadOnlyList<AccessSource> sources) => sources.Count == 0
        ? "žádný zdroj"
        : string.Join("; ", sources.Select(source => SourceLabel(source.Kind) + (source.Name.Length > 0 ? " přes skupinu " + McpText.Quote(McpText.Line(source.Name, 120)) : "")
            + " (" + source.Role + Validity(source.StartAt, source.ExpiresAt) + (source.Effective ? "" : ", teď neplatí") + ")"));

    private static JsonArray SourceNodes(IReadOnlyList<AccessSource> sources) => new(sources.Select(source => (JsonNode)new JsonObject
    {
        ["kind"] = source.Kind, ["group"] = source.Name.Length > 0 ? source.Name : null, ["groupId"] = source.GroupId, ["role"] = source.Role,
        ["startAt"] = source.StartAt, ["expiresAt"] = source.ExpiresAt, ["effective"] = source.Effective
    }).ToArray());

    private static string Validity(DateTime? start, DateTime? expires)
        => (start is { } from ? ", od " + Date(from) : "") + (expires is { } until ? ", do " + Date(until) : "");

    private static string SourceLabel(string kind) => kind switch
    {
        "global" => "správce wiki",
        "spaceAdmin" => "správce prostoru",
        "space" => "výchozí přístup prostoru",
        "page" => "přímý přístup ke stránce",
        _ => kind
    };

    private static string DenialLabel(string denial) => denial switch
    {
        "inactive" => "účet není aktivní",
        "privateDraft" => "nepublikovaná stránka jiného autora",
        "ownAccess" => "stránka má vlastní přístupy, které výchozí přístup prostoru skrývají",
        "none" => "žádné oprávnění se na účet nevztahuje",
        _ => denial
    };

    private static string Date(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private async Task<CallToolResult> RunAsync(string tool, Func<Task<CallToolResult>> body, CancellationToken ct)
    {
        try
        {
            return await body();
        }
        catch (ApiException error)
        {
            return McpErrors.From(error);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never the exception text: an ORM or driver message can quote statement parameters, which hold tool arguments.
            m_logger.LogError("MCP tool {Tool} failed ({ErrorType})", tool, error.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
    }
}
