using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageVerifications.Commands;
using Atlas.Server.Features.PageVerifications.Queries;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.PageVerifications;

public sealed class PageVerificationMcpHandler(McpOrganizationGuard guard, PageVerificationHandler handler, PageVerificationQueryHandler queries,
    SpaceReadershipHandler readership)
{
    public async Task<PageVerificationView> Handle(SetMcpVerification command, CancellationToken ct)
    {
        await guard.LockPageSpaceAsync(command.PageId, ct);
        return await handler.Handle(new SetPageVerification(command.PageId, command.Request), ct);
    }

    public async Task<SpaceVerificationResult> Handle(SetMcpSpaceVerification command, CancellationToken ct)
    {
        await guard.LockSpaceForWriteAsync(command.SpaceId, ct);
        return await handler.Handle(new SetSpaceVerification(command.SpaceId, command.Request), ct);
    }

    public async Task<ContentHealthList> Handle(GetMcpContentHealth query, CancellationToken ct)
    {
        await guard.LockSpaceForReadAsync(query.Query.SpaceId, ct);
        return await queries.Handle(query.Query, ct);
    }

    public async Task<SpaceReadership> Handle(GetMcpSpaceReadership query, CancellationToken ct)
    {
        await guard.LockSpaceForReadAsync(query.Query.SpaceId, ct);
        return await readership.Handle(query.Query, ct);
    }

    public async Task<PageOutdatedReport> Handle(ReportMcpOutdated command, CancellationToken ct)
    {
        await guard.LockPageSpaceSharedAsync(command.Command.PageId, ct);
        return await handler.Handle(command.Command, ct);
    }
}

/// <summary>5.2 over MCP (plan §7): what a person does with verification, AI does under the token's owner with the same rights; a
/// verification over MCP is marked „přes AI“. Not a Wolverine handler.</summary>
[McpServerToolType]
public sealed class PageVerificationMcpTools(IMessageBus bus)
{
    private const string States = "Stavy: verified (ověřeno), expiring (vyprší do 14 dní), expired (vypršelo), unverified (neověřeno), off (stránka se neověřuje).";

    [McpServerTool(Name = "atlas_set_verification", Title = "Ověřit stránku nebo nastavit garanta", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Ověří publikovanou stránku (verify), nastaví garanta (stewardId nebo stewardFromLastAuthor), zruší ho (clearSteward) nebo interval ověření. pageId, nebo spaceId s pageIds hromadně.")]
    [Description("Ověří publikovanou verzi stránky (verify: true, volitelně until = datum platnosti, jinak dnes + interval), nastaví garanta " +
        "(stewardId = id uživatele, který smí stránku číst; stewardFromLastAuthor: true = autor poslední publikované verze; clearSteward: true = bez garanta) nebo interval ověření " +
        "(intervalMonths: inherit = podle prostoru, 0 = neověřovat, 3, 6, 12). Hromadně: spaceId a pageIds (až 500; verify, stewardId, " +
        "clearSteward, stewardFromLastAuthor = jen stránkám bez garanta), spaceId bez pageIds se stewardFromLastAuthor nastaví garanta všem ověřovaným stránkám prostoru bez garanta. " +
        "Smí každý s právem úprav stránky; ověření se označí „přes AI“. Šablony se neověřují. Přes MCP vyžaduje rozsah Zápis. " + States)]
    public async Task<CallToolResult> SetVerificationAsync(
        [Description("pageId stránky (nebo spaceId s pageIds pro hromadnou změnu).")] string? pageId = null,
        [Description("true = ověřit publikovanou verzi.")] bool? verify = null,
        [Description("Datum, do kdy ověření platí (ISO 8601; samotné datum platí do konce dne v čase Europe/Prague), jen s verify.")] string? until = null,
        [Description("Id uživatele, který bude garantem.")] string? stewardId = null,
        [Description("true = garantem bude autor poslední publikované verze.")] bool? stewardFromLastAuthor = null,
        [Description("true = stránka nebude mít garanta.")] bool? clearSteward = null,
        [Description("inherit (podle prostoru), 0 (neověřovat), 3, 6 nebo 12.")] string? intervalMonths = null,
        [Description("Pro hromadnou změnu: spaceId prostoru.")] string? spaceId = null,
        [Description("Pro hromadnou změnu: pageIds stránek prostoru.")] string[]? pageIds = null,
        CancellationToken ct = default)
    {
        var page = McpArguments.OptionalId(pageId, "pageId");
        var space = McpArguments.OptionalId(spaceId, "spaceId");
        if ((page is null) == (space is null))
        {
            return McpErrors.InvalidArgument("Zadejte pageId, nebo spaceId pro hromadnou změnu.");
        }
        DateTime? end = null;
        if (until is not null)
        {
            // As the web's JSON: a date or a time without a zone stays unspecified, and the shared handler reads it in Europe/Prague.
            if (!DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return McpErrors.InvalidArgument("until musí být datum ISO 8601.");
            }
            end = parsed;
        }
        int? months = null;
        if (intervalMonths is not null and not "inherit")
        {
            if (!int.TryParse(intervalMonths, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMonths))
            {
                return McpErrors.InvalidArgument("intervalMonths je inherit, 0, 3, 6 nebo 12.");
            }
            months = parsedMonths;
        }
        var request = new SetPageVerificationRequest(verify, end, McpArguments.OptionalId(stewardId, "stewardId"), stewardFromLastAuthor, months,
            intervalMonths == "inherit" ? true : null, clearSteward);
        if (space is not null && (months is not null || intervalMonths is not null || end is not null))
        {
            return McpErrors.InvalidArgument("Hromadně lze jen ověřit a nastavit garanta; interval a datum nastavte po stránkách.");
        }
        var ids = pageIds?.Select((id, index) => McpArguments.RequiredId(id, $"pageIds[{index}]")).ToList();
        if (space is { } spaceToChange)
        {
            // One action per call, as Stav obsahu sends it.
            var steward = (request.StewardId is not null ? 1 : 0) + (clearSteward == true ? 1 : 0);
            var action = verify == true && steward == 0 && stewardFromLastAuthor != true ? "verify"
                : verify != true && steward == 1 && stewardFromLastAuthor != true ? "steward"
                : verify != true && steward == 0 && stewardFromLastAuthor == true ? "stewardFromLastAuthor"
                : null;
            if (action is null)
            {
                return McpErrors.InvalidArgument("Hromadně zadejte právě jedno: verify, stewardId, clearSteward, nebo stewardFromLastAuthor.");
            }
            var bulk = await bus.InvokeAsync<SpaceVerificationResult>(new SetMcpSpaceVerification(spaceToChange,
                new SetSpaceVerificationRequest(action, ids, request.StewardId)), ct);
            return McpText.Result($"Hromadná změna ověření: změněno {bulk.Changed} stránek, přeskočeno {bulk.Skipped.Count}" +
                (bulk.Skipped.Count > 0 ? " (" + string.Join(", ", bulk.Skipped.Take(20).Select(x => $"{x.PageId}: {x.Reason}")) + ")." : ".") +
                (bulk.More ? " Zbývají další stránky bez garanta, spusťte znovu." : ""),
                new JsonObject
                {
                    ["changed"] = bulk.Changed,
                    ["more"] = bulk.More,
                    ["skipped"] = new JsonArray(bulk.Skipped.Select(x => (JsonNode)new JsonObject { ["pageId"] = x.PageId, ["reason"] = x.Reason }).ToArray())
                });
        }
        var view = await bus.InvokeAsync<PageVerificationView>(new SetMcpVerification(page!.Value, request), ct);
        var structured = PageVerificationText.Json(view);
        structured["pageId"] = page;
        structured["intervalMonths"] = view.IntervalMonths;
        structured["spaceIntervalMonths"] = view.SpaceIntervalMonths;
        return McpText.Result("Ověření stránky uloženo.\n- pageId: " + page + "\n- ověření: " + (PageVerificationText.Line(view) ?? "stránka se neověřuje"), structured);
    }

    [McpServerTool(Name = "atlas_list_content_health", Title = "Stav obsahu prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Stav obsahu prostoru: ověření a čtenost stránek, nejdřív čtené a neověřené. spaceId, volitelně state, mine, sort, days, query, cursor.")]
    [Description("Stav obsahu prostoru (pro správce a editory prostoru): počty stránek podle stavu ověření a publikované stránky se stavem, " +
        "garantem, datem vypršení a čteností (zobrazení a různí čtenáři za období, bez jmen). state: all, due (k ověření), no-steward, " +
        "unread (nečtené v období); mine: jen kde jsem garant; sort: priority (výchozí: nejdřív k ověření, pak nejčtenější), expiry, readers, " +
        "published; days: 30, 90 (výchozí), 365. " + States + " " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListContentHealthAsync(
        [Description("spaceId prostoru.")] string spaceId,
        [Description("all, due, no-steward, unread.")] string? state = null,
        [Description("true = jen stránky, kde jsem garant.")] bool mine = false,
        [Description("priority, expiry, readers, published.")] string? sort = null,
        [Description("Období čtenosti: 30, 90 nebo 365 dní.")] int days = 90,
        [Description("Část názvu stránky.")] string? query = null,
        [Description("Počet položek 1–50, výchozí 20.")] int limit = 20,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
    {
        var space = McpArguments.RequiredId(spaceId, "spaceId");
        if (limit is < 1 or > 50)
        {
            return McpErrors.InvalidArgument("limit musí být 1 až 50.");
        }
        var list = await bus.InvokeAsync<ContentHealthList>(new GetMcpContentHealth(new GetContentHealth(space, state, mine, sort, days, query, cursor, limit)), ct);
        var text = new StringBuilder();
        var s = list.Summary;
        text.Append("Stav obsahu: ověřeno ").Append(s.Verified).Append(", brzy vyprší ").Append(s.Expiring).Append(", vypršelo ")
            .Append(s.Expired).Append(", neověřeno ").Append(s.Unverified).Append(", bez garanta ").Append(s.WithoutSteward).Append(". Stránek v seznamu: ").Append(list.Total).Append(".\n");
        var items = new JsonArray();
        foreach (var item in list.Items)
        {
            text.Append("- ").Append(McpText.Line(item.Title, 200)).Append(" (pageId ").Append(item.PageId).Append(")\n   cesta: ")
                .Append(McpText.Line(string.Join(" › ", item.Path.Append(item.Title)), 400)).Append("\n   stav: ").Append(item.State)
                .Append(item.VerifiedUntil is { } until ? ", do " + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")
                .Append(item.ChangedSinceVerification ? ", změněno po ověření" : "")
                .Append(", garant: ").Append(item.StewardName is { } name ? McpText.Line(name, 100) : "bez garanta")
                .Append("\n   čtenost za ").Append(days).Append(" dní: ").Append(item.Readers).Append(" čtenářů, ").Append(item.Views).Append(" zobrazení\n");
            items.Add(new JsonObject
            {
                ["pageId"] = item.PageId, ["title"] = item.Title, ["path"] = new JsonArray(item.Path.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["state"] = item.State, ["stewardId"] = item.StewardId,
                ["stewardName"] = item.StewardName, ["verifiedUntil"] = item.VerifiedUntil, ["changedSinceVerification"] = item.ChangedSinceVerification,
                ["readers"] = item.Readers, ["views"] = item.Views, ["lastViewedAt"] = item.LastViewedAt, ["lastPublishedAt"] = item.LastPublishedAt
            });
        }
        if (list.NextCursor is { } next)
        {
            text.Append("Další stránky: zavolejte atlas_list_content_health se stejnými parametry a cursor: \"").Append(next).Append("\".\n");
        }
        var structured = new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["verified"] = s.Verified, ["expiring"] = s.Expiring, ["expired"] = s.Expired, ["unverified"] = s.Unverified, ["withoutSteward"] = s.WithoutSteward
            },
            ["enabled"] = list.Enabled, ["total"] = list.Total, ["items"] = items, ["nextCursor"] = list.NextCursor
        };
        return McpText.Result(text.ToString(), structured);
    }

    [McpServerTool(Name = "atlas_list_readership", Title = "Čtenost prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Čtenost publikovaných stránek prostoru za období (zobrazení, různí čtenáři, bez jmen). spaceId, from, to, volitelně filter, cursor.")]
    [Description("Čtenost publikovaných stránek prostoru za období nejvýše 366 dní (pro správce a editory prostoru): u každé stránky zobrazení, " +
        "počet různých čtenářů, poslední zobrazení a poslední publikace, nejčtenější první, po 50. Jen počty, nikdy kdo četl. from a to jsou dny " +
        "(ISO 8601, např. 2026-11-01) v pražském čase včetně; filter: all (výchozí) nebo unread (bez zobrazení v období). " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListReadershipAsync(
        [Description("spaceId prostoru.")] string spaceId,
        [Description("První den období (ISO 8601).")] string from,
        [Description("Poslední den období (ISO 8601).")] string to,
        [Description("all nebo unread.")] string? filter = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
    {
        var space = McpArguments.RequiredId(spaceId, "spaceId");
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return McpErrors.InvalidArgument("from a to musí být datum ve tvaru RRRR-MM-DD.");
        }
        var list = await bus.InvokeAsync<SpaceReadership>(new GetMcpSpaceReadership(new GetSpaceReadership(space, start, end, filter, cursor)), ct);
        var text = new StringBuilder();
        var items = new JsonArray();
        foreach (var item in list.Items)
        {
            text.Append("- ").Append(McpText.Line(item.Title, 200)).Append(" (pageId ").Append(item.PageId).Append("): ").Append(item.Readers)
                .Append(" čtenářů, ").Append(item.Views).Append(" zobrazení")
                .Append(item.LastViewedAt is { } viewed ? ", naposledy " + viewed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ", v období nečteno")
                .Append('\n');
            items.Add(new JsonObject
            {
                ["pageId"] = item.PageId, ["title"] = item.Title, ["views"] = item.Views, ["readers"] = item.Readers,
                ["lastViewedAt"] = item.LastViewedAt, ["lastPublishedAt"] = item.LastPublishedAt
            });
        }
        if (list.NextCursor is { } next)
        {
            text.Append("Další stránky: zavolejte atlas_list_readership se stejnými parametry a cursor: \"").Append(next).Append("\".\n");
        }
        return McpText.Result(McpText.Wrap($"Čtenost prostoru {space} od {from} do {to}: {list.Items.Count} stránek.", text.ToString()),
            new JsonObject { ["spaceId"] = space, ["items"] = items, ["nextCursor"] = list.NextCursor });
    }

    [McpServerTool(Name = "atlas_report_outdated", Title = "Nahlásit neaktuální stránku", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Nahlásí garantovi stránky, že je neaktuální, s volitelnou poznámkou. pageId, note.")]
    [Description("Nahlásí stránku jako neaktuální: garant stránky (bez garanta až tři editoři, kteří ji naposledy publikovali) dostane oznámení " +
        "s poznámkou (nejvýše 500 znaků). Smí každý čtenář stránky; stránka se nemění. Přes MCP vyžaduje rozsah Zápis.")]
    public async Task<CallToolResult> ReportOutdatedAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("Volitelná poznámka: co je neaktuální.")] string? note = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(pageId, "pageId");
        var report = await bus.InvokeAsync<PageOutdatedReport>(new ReportMcpOutdated(new ReportPageOutdated(id, new ReportPageOutdatedRequest(note))), ct);
        return McpText.Result($"Nahlášeno: oznámení dostalo {report.Notified} lidí.", new JsonObject { ["pageId"] = id, ["notified"] = report.Notified });
    }
}
