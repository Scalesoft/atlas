using Atlas.Server.Features.Skills.Commands;
using Atlas.Server.Features.Skills.Queries;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.Skills;

/// <summary>
/// Mapa znalostí over MCP and for the assistant (AI parity, docs/skills-design.md §6): who knows a topic, what a person knows, rating, merging
/// and linking topics as the token owner. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class SkillMcpTools(IMessageBus bus)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "atlas_find_experts", Title = "Mapa znalostí: kdo umí", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Kdo umí téma (technologii, oblast): topic, volitelně context (zákazník, projekt, prostor: „Orion“) a role. Pro otázky „kdo dělal/umí X“.")]
    [Description("Mapa znalostí: najde lidi, kteří umí téma (štítek, i podle aliasu nebo jiného zápisu, včetně užších témat: .NET najde i C#): jejich úroveň 1–5 " +
        "(vlastní odhad a průměr hodnocení kolegů od tří hodnocení), poznámky „jak“ a stránky k tématu, které psali nebo na nich jsou zmínění. " +
        "context (prostor, štítek nebo organizace, např. zákazník) zúží stránky a jeho lidi dá první; role filtruje podle funkce kontaktu. " +
        "Odpovídejte jménem, funkcí a tím, jak téma umí („Pavel Horák – vývojář; konfiguračně je umí konzultant Tomáš Kolář“). " +
        "Kdo komu dal kolik, nástroj neprozrazuje. " + McpText.TrustNotice)]
    public async Task<CallToolResult> FindExpertsAsync(
        [Description("Téma nebo technologie, např. „informované souhlasy“, „C#“.")] string topic,
        [Description("Volitelně kontext: prostor, štítek nebo organizace (zákazník, projekt).")] string? context = null,
        [Description("Volitelně role (funkce kontaktu): „vývojář“, „konzultant“.")] string? role = null,
        [Description("Počet lidí 1–50, výchozí 10.")] int limit = 10,
        CancellationToken ct = default)
    {
        var list = await bus.InvokeAsync<ExpertList>(new FindExperts(topic, context, role, limit, AiScopeOnly: true), ct);
        if (list.Topic is not { } found)
        {
            return McpText.Result(McpText.Wrap("Téma nenalezeno.", list.Similar.Count == 0 ? "Žádné podobné téma."
                : "Podobná témata: " + string.Join(", ", list.Similar.Select(x => McpText.Line(x.Name, 60)))), JsonSerializer.SerializeToNode(list, Json)!.AsObject());
        }
        var body = new StringBuilder();
        body.Append("Téma: ").Append(McpText.Line(found.Name, 60));
        if (found.Aliases.Count > 0) body.Append(" (také ").Append(string.Join(", ", found.Aliases.Select(x => McpText.Line(x, 60)))).Append(')');
        if (found.Parents.Count > 0) body.Append("; součást ").Append(string.Join(", ", found.Parents.Select(x => McpText.Line(x.Name, 60))));
        if (found.Children.Count > 0) body.Append("; užší témata ").Append(string.Join(", ", found.Children.Select(x => McpText.Line(x.Name, 60))));
        body.Append(" (tagId ").Append(found.Id).Append(")\n");
        var number = 0;
        foreach (var person in list.People)
        {
            body.Append(++number).Append(". ").Append(McpText.Line(person.Contact["name"]?.ToString(), 200));
            var about = new[] { person.Contact["title"]?.ToString(), person.Contact["organization"]?.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (about.Length > 0) body.Append(" – ").Append(McpText.Line(string.Join(", ", about), 200));
            body.Append(" (contactId ").Append(person.ContactId).Append(")\n   ").Append(Describe(person.Skill, person.Via));
            if (person.InContext) body.Append(" · v kontextu");
            if (person.Evidence > 0)
            {
                body.Append(" · stránky k tématu: ").Append(person.Evidence).Append(" (")
                    .Append(string.Join("; ", person.Pages.Select(x => $"{McpText.Line(x.Title, 120)} – {McpText.Line(x.SpaceName, 60)}, pageId {x.PageId}"))).Append(')');
            }
            body.Append('\n');
        }
        if (list.Similar.Count > 0) body.Append("Možná totéž: ").Append(string.Join(", ", list.Similar.Select(x => $"{McpText.Line(x.Name, 60)} (tagId {x.Id})"))).Append('\n');
        return McpText.Result(McpText.Wrap($"Lidé k tématu {McpText.Line(found.Name, 60)}: {number}.", number == 0 ? body.Append("Nikdo.").ToString() : body.ToString()),
            JsonSerializer.SerializeToNode(list, Json)!.AsObject());
    }

    [McpServerTool(Name = "atlas_get_skills", Title = "Co člověk umí", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Co umí osoba z adresáře: contactId (atlas_get_contact, atlas_find_experts). Témata s úrovní 1–5 a poznámkami.")]
    [Description("Mapa znalostí: vypíše, co umí osoba (kontakt adresáře): témata s úrovní 1–5 (vlastní odhad a průměr kolegů od tří hodnocení), vaše vlastní hodnocení, " +
        "podepsaná hodnocení s poznámkami a kolik stránek k tématu napsala. U vlastního kontaktu i navržená témata ze stránek, které jste psali. " + McpText.TrustNotice)]
    public async Task<CallToolResult> GetSkillsAsync([Description("contactId osoby.")] string contactId, CancellationToken ct = default)
    {
        var skills = await bus.InvokeAsync<ContactSkills>(new GetContactSkills(McpArguments.RequiredId(contactId, "contactId"), AiScopeOnly: true), ct);
        var body = new StringBuilder();
        foreach (var skill in skills.Skills)
        {
            body.Append("- ").Append(McpText.Line(skill.Name, 60)).Append(": ").Append(Describe(skill, null)).Append(" (tagId ").Append(skill.TagId).Append(")\n");
        }
        if (skills.Suggested.Count > 0)
        {
            body.Append("Navržená témata (ze stránek, které osoba psala; jen návrh): ").Append(string.Join(", ", skills.Suggested.Select(x => $"{McpText.Line(x.Name, 60)} ({x.People} stránek)"))).Append('\n');
        }
        return McpText.Result(McpText.Wrap($"Co umí {McpText.Line(skills.Name, 200)}: {skills.Skills.Count} témat.", skills.Skills.Count == 0 && skills.Suggested.Count == 0 ? "Zatím nic." : body.ToString()),
            JsonSerializer.SerializeToNode(skills, Json)!.AsObject());
    }

    [McpServerTool(Name = "atlas_rate_skill", Title = "Ohodnotit znalost", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Zapíše hodnocení znalosti osoby jménem uživatele: contactId, topic, level 1–5 (null smaže), visibility, note. Jen na pokyn uživatele.")]
    [Description("Ohodnotí osobu (contactId) v tématu jménem vlastníka tokenu: topic textem (existující téma, alias nebo jiný zápis se spojí s ním, jinak vznikne nové), " +
        "level 1–5 (1 základy, 2 pracuje s tím, 3 samostatně, 4 do hloubky, 5 učí ostatní) nebo null hodnocení smaže, visibility private (jen pro vás), " +
        "shared (anonymně do souhrnu, výchozí) nebo signed (s vaším jménem), note jak téma umí (např. „konfiguračně“). Vlastní kontakt = sebehodnocení. " +
        "Zapisujte jen to, co uživatel řekl; nikdy hodnocení neodvozujte sami. Ostatním se změní hned.")]
    public async Task<CallToolResult> RateAsync(
        [Description("contactId osoby.")] string contactId,
        [Description("Téma textem.")] string topic,
        [Description("Úroveň 1–5, null hodnocení smaže.")] int? level,
        [Description("private, shared (výchozí) nebo signed.")] string? visibility = null,
        [Description("Volitelně jak téma umí, jeden řádek do 200 znaků.")] string? note = null,
        CancellationToken ct = default)
    {
        var skills = await bus.InvokeAsync<ContactSkills>(new RateSkill(McpArguments.RequiredId(contactId, "contactId"), new RateSkillRequest(topic, level, visibility, note)), ct);
        return McpText.Result(level is null ? "Hodnocení smazáno." : "Hodnocení uloženo.", JsonSerializer.SerializeToNode(skills, Json)!.AsObject());
    }

    [McpServerTool(Name = "atlas_merge_topics", Title = "Sloučit témata", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Sloučí téma tagId do intoId (různé zápisy téhož); bez confirm: true jen náhled. confirm až po potvrzení člověka.")]
    [Description("Sloučí dvě témata (štítky), která jsou totéž (např. „C Sharp“ do „C#“): název slučovaného se stane aliasem, hodnocení lidí, vazby a štítky " +
        "stránek přejdou na cílové. Kandidáty vrací atlas_find_experts jako Možná totéž. Bez confirm: true jen náhled, nic se nemění; confirm nastavte, " +
        "až sloučení potvrdí člověk. Voláním nejde vrátit.")]
    public async Task<CallToolResult> MergeAsync(
        [Description("tagId tématu, které zanikne.")] string tagId,
        [Description("tagId tématu, které zůstane.")] string intoId,
        [Description("true až po potvrzení člověka; jinak jen náhled.")] bool confirm = false,
        CancellationToken ct = default)
    {
        var (from, into) = (McpArguments.RequiredId(tagId, "tagId"), McpArguments.RequiredId(intoId, "intoId"));
        if (!confirm)
        {
            return McpText.Result("Náhled, nic se nezměnilo. Po potvrzení člověka zavolejte znovu s confirm: true.",
                new JsonObject { ["confirmed"] = false, ["tagId"] = from, ["intoId"] = into });
        }
        var topic = await bus.InvokeAsync<TopicView>(new MergeTopics(from, into), ct);
        return McpText.Result($"Témata sloučena do {McpText.Line(topic.Name, 60)} (tagId {topic.Id}); aliasy: {string.Join(", ", topic.Aliases)}.",
            JsonSerializer.SerializeToNode(topic, Json)!.AsObject());
    }

    [McpServerTool(Name = "atlas_link_topics", Title = "Téma je součástí", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Označí téma tagId jako součást širšího parentId (C# součástí .NET), s kind: related jako související; remove: true vazbu zruší.")]
    [Description("Označí téma (tagId) jako součást širšího tématu (parentId), např. C# je součástí .NET: kdo hledá širší téma, najde i lidi s užším. " +
        "remove: true vazbu zruší. Téma nemůže být součástí sebe ani svého podtématu.")]
    public async Task<CallToolResult> LinkAsync(
        [Description("tagId užšího tématu.")] string tagId,
        [Description("tagId širšího tématu.")] string parentId,
        [Description("true vazbu zruší.")] bool remove = false,
        [Description("part_of (je součástí, výchozí) nebo related (souvisí).")] string kind = "part_of",
        CancellationToken ct = default)
    {
        var topic = await bus.InvokeAsync<TopicView>(new LinkTopics(McpArguments.RequiredId(tagId, "tagId"), McpArguments.RequiredId(parentId, "parentId"), remove, kind), ct);
        return McpText.Result($"Téma {McpText.Line(topic.Name, 60)} je součástí: {(topic.Parents.Count == 0 ? "ničeho" : string.Join(", ", topic.Parents.Select(x => x.Name)))}.",
            JsonSerializer.SerializeToNode(topic, Json)!.AsObject());
    }

    private static string Describe(SkillView? skill, string? via)
    {
        if (skill is null)
        {
            return "bez hodnocení (pracoval/a na stránkách k tématu)";
        }
        var parts = new List<string>();
        if (via is not null) parts.Add($"přes užší téma {McpText.Line(via, 60)}");
        if (skill.Score is { } score) parts.Add($"úroveň {score.ToString("0.#", CultureInfo.GetCultureInfo("cs-CZ"))}/5");
        if (skill.Self is { } self) parts.Add($"vlastní odhad {self}");
        if (skill.Peers > 0) parts.Add($"hodnotili kolegové: {skill.Peers}");
        else if (skill.FewPeers) parts.Add("hodnotili 1–2 kolegové");
        if (skill.Mine is { } mine) parts.Add($"vaše hodnocení {mine} ({skill.MyVisibility}){(skill.MyNote is { Length: > 0 } own ? $": {McpText.Line(own, 200)}" : "")}");
        if (skill.SelfNote is { Length: > 0 } note) parts.Add($"jak: {McpText.Line(note, 200)}");
        foreach (var rater in skill.Raters) parts.Add($"{McpText.Line(rater.Name, 100)}: {rater.Level}{(rater.Note is { Length: > 0 } text ? $" ({McpText.Line(text, 200)})" : "")}");
        return string.Join(" · ", parts);
    }
}
