using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Objects;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The overview over MCP (plan 3.4 R6, AI parity of the web's overview block and its actions, tests/fixtures/content/overview-mcp.json):
/// what an overview of a kind shows on a page or a whole board, collected as the web's kinds collect it (OverviewContent; a board is its
/// blocks, then its live contact cards and the people its texts mention, its product and service cards' requirements), with the
/// action's form: contacts as vCard text, issues as a list with their keys, requirements as the summary line.
/// </summary>
public sealed partial class McpOverviewTools
{
    [GeneratedRegex(@"\]\(a-contact:\?([^)\s]*)\)")]
    private static partial Regex ContactMention();

    [McpServerTool(Name = "atlas_get_overview", Title = "Přehled stránky nebo tabule", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Obsah přehledu (blok ```a-overview) celé stránky nebo tabule: requirements = stav nasazení, contacts = kontakty i jako vCard, issues = úkoly " +
        "YouTrack s klíči. kind a pageId nebo boardId.")]
    [Description("Vrátí, co ukazuje přehled druhu kind na celé stránce (pageId) nebo tabuli (boardId), jako přehled ve webu, i když tam blok přehledu není: " +
        "requirements souhrn nasazení (na tabuli z bloků požadavků a z karet produktů a služeb), contacts každého člověka jednou (kartičky a zmínky, na tabuli i karty " +
        "kontaktů adresáře) a k tomu všechny jako text vCard (to, co web stáhne jako .vcf), issues každý úkol YouTrack jednou podle klíče z uloženého stavu (klíč, " +
        "název, stav, odkaz) a řádek klíčů (to, co web zkopíruje nebo stáhne jako .csv). Přehled do stránky vložíte blokem ```a-overview (atlas_insert_section), " +
        "na tabuli blokem {\"type\":\"overview\",\"attrs\":{\"kind\":…}} (atlas_edit_board). " + McpText.TrustNotice)]
    public Task<CallToolResult> GetOverviewAsync(
        [Description("Druh přehledu: requirements, contacts nebo issues.")] string kind,
        [Description("pageId stránky (nebo boardId).")] string? pageId = null,
        [Description("boardId tabule (nebo pageId).")] string? boardId = null,
        [Description("U stránky \"published\" (výchozí) nebo \"working\" (pracovní verze, jen editorovi stránky).")] string version = "published",
        CancellationToken ct = default)
        => RunAsync("atlas_get_overview", async () =>
        {
            if (!OverviewContent.Kinds.Contains(kind))
            {
                return McpErrors.InvalidArgument("kind musí být requirements, contacts nebo issues.");
            }
            if (string.IsNullOrEmpty(pageId) == string.IsNullOrEmpty(boardId))
            {
                return McpErrors.InvalidArgument("Zadejte pageId, nebo boardId.");
            }
            if (version is not ("published" or "working"))
            {
                return McpErrors.InvalidArgument("version musí být \"published\" nebo \"working\".");
            }
            JsonNode document;
            string where;
            if (!string.IsNullOrEmpty(pageId))
            {
                var id = McpArguments.RequiredId(pageId, "pageId");
                document = await PageDocumentAsync(id, version == "working", ct) ?? new JsonObject();
                where = $"stránky {id}";
            }
            else
            {
                if (m_types.Find(BoardsAtlasModule.ContentType) is null)
                {
                    throw new ApiException(409, "Přehled tabule potřebuje zapnutý modul Tabule.", "boards_disabled");
                }
                var board = await m_bus.InvokeAsync<BoardDto>(new GetBoard(McpArguments.RequiredId(boardId, "boardId")), ct);
                document = await BoardDocumentAsync(kind, board, ct);
                where = $"tabule {McpText.Line(board.Title, 200)} ({board.Id})";
            }
            var body = new StringBuilder();
            var structured = new JsonObject { ["kind"] = kind };
            switch (kind)
            {
                case OverviewContent.Requirements:
                    var line = "**" + RequirementText.SummaryLabel + ":** " + RequirementText.Summary(document);
                    body.Append(line).Append('\n');
                    structured["summary"] = line;
                    break;
                case OverviewContent.Contacts:
                    var people = OverviewContent.People(document);
                    foreach (var person in people)
                    {
                        body.Append("- ").Append(McpText.Line(string.Join(" · ", new[] { "name", "title", "organization" }.Select(key => person[key]?.ToString() ?? "")
                            .Concat((person["emails"] as JsonArray ?? []).Take(1).Select(email => email?.ToString() ?? "")).Where(text => text.Length > 0)), 400));
                        if (person["objectId"] is { } objectId)
                        {
                            body.Append(" (contactId: ").Append(McpText.Line(objectId.ToString(), 60)).Append(')');
                        }
                        body.Append('\n');
                    }
                    var vcard = ContactExport.VCard(people);
                    if (people.Count > 0)
                    {
                        body.Append("\nvCard:\n").Append(McpText.Content(vcard.Replace("\r\n", "\n", StringComparison.Ordinal)));
                    }
                    structured["contacts"] = new JsonArray(people.Select(person => (JsonNode)person.DeepClone()).ToArray());
                    structured["vcard"] = vcard;
                    break;
                default:
                    var issues = OverviewContent.IssuesOf(document);
                    foreach (var issue in issues)
                    {
                        body.Append("- ").Append(McpText.Line(issue.Key + ": " + issue.Title + (issue.State.Length > 0 ? " · " + issue.State : ""), 400))
                            .Append(issue.Url.Length > 0 ? " (" + McpText.Line(issue.Url, 300) + ")" : "").Append('\n');
                    }
                    var keys = string.Join(", ", issues.Select(issue => issue.Key));
                    if (issues.Count > 0)
                    {
                        body.Append("\nKlíče: ").Append(McpText.Line(keys, 4000)).Append('\n');
                    }
                    structured["issues"] = JsonSerializer.SerializeToNode(issues, McpJson);
                    structured["keys"] = keys;
                    break;
            }
            return McpText.Result(McpText.Wrap($"Přehled {kind} {where}.", body.Length > 0 ? body.ToString() : "(prázdný)"), structured);
        }, ct);

    private static readonly JsonSerializerOptions McpJson = new(JsonSerializerDefaults.Web);

    /// <summary>A page's stored document (the reader's rights and G5, GetMcpPage); null for a page the reader cannot see.</summary>
    private async Task<JsonNode?> PageDocumentAsync(Guid pageId, bool working, CancellationToken ct)
    {
        var read = await m_bus.InvokeAsync<McpPageRead>(new GetMcpPage(pageId, working), ct);
        return JsonNode.Parse(read.Page.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)?["document"];
    }

    /// <summary>The board as one document for <paramref name="kind"/> (overviewKinds.ts <c>boardDocument</c>): its blocks, then what its other items stand for.</summary>
    private async Task<JsonNode> BoardDocumentAsync(string kind, BoardDto board, CancellationToken ct)
    {
        var content = new JsonArray(board.Items.Where(item => item.Kind == "block" && item.Props.ValueKind == JsonValueKind.Object && item.Props.TryGetProperty("block", out _))
            .Select(item => JsonNode.Parse(item.Props.GetProperty("block").GetRawText())).ToArray());
        var document = new JsonObject { ["type"] = "doc", ["content"] = content };
        if (kind == OverviewContent.Contacts)
        {
            // Directory contact cards and people the texts mention (pageContacts.ts boardContactNodes), each as a block of its live contact.
            var known = OverviewContent.People(document).Select(person => person["objectId"]?.ToString().ToLowerInvariant()).ToHashSet();
            var ids = new List<Guid>();
            foreach (var item in board.Items)
            {
                if (item.Kind == "object" && Prop(item.Props, "typeId") == ContactObjectType.Id && Guid.TryParse(Prop(item.Props, "objectId"), out var objectId))
                {
                    ids.Add(objectId);
                }
                foreach (Match mention in ContactMention().Matches(item.Text ?? ""))
                {
                    if (Guid.TryParse(System.Web.HttpUtility.ParseQueryString(mention.Groups[1].Value)["objectId"], out var mentioned))
                    {
                        ids.Add(mentioned);
                    }
                }
            }
            var wanted = ids.Distinct().Where(id => !known.Contains(id.ToString())).ToList();
            if (wanted.Count > 0)
            {
                // LiveContacts takes at most 200 ids a call.
                var live = new Dictionary<Guid, Contacts.Persistence.LiveContact>();
                foreach (var chunk in wanted.Chunk(200))
                {
                    foreach (var (key, value) in (await m_bus.InvokeAsync<LiveContactsResult>(new LiveContacts(chunk), ct)).Items)
                    {
                        live[key] = value;
                    }
                }
                foreach (var id in wanted)
                {
                    if (live.TryGetValue(id, out var found) && found is { State: "ok", Contact: { } contact })
                    {
                        content.Add(new JsonObject { ["type"] = "contactCard", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(contact.DeepClone()) } } });
                    }
                }
            }
        }
        if (kind == OverviewContent.Requirements)
        {
            // The product and service cards' requirements (deploymentMap.ts cardRequirementNodes), each block of a page once.
            foreach (var cards in board.Items.Where(item => item.Kind == "page" && item.Target is { } && Prop(item.Props, "display") is "product" or "service")
                .GroupBy(item => item.Target!.ResourceId))
            {
                JsonNode? page;
                try
                {
                    page = await PageDocumentAsync(cards.Key, false, ct);
                }
                catch (ApiException)
                {
                    continue;
                }
                var map = DeploymentBoard.Of(page);
                var blocks = new Dictionary<string, DeploymentBoard.Block>(StringComparer.Ordinal);
                foreach (var card in cards)
                {
                    var anchor = Prop(card.Props, "anchor");
                    var shown = Prop(card.Props, "display") == "product" ? map.Products.FirstOrDefault(product => product.Anchor == anchor)?.Blocks
                        : map.Services.FirstOrDefault(service => service.Anchor == anchor)?.Inputs;
                    foreach (var block in shown ?? [])
                    {
                        blocks.TryAdd(block.Id, block);
                    }
                }
                foreach (var block in blocks.Values)
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "requirement", ["attrs"] = block.Attrs?.DeepClone(),
                        ["content"] = new JsonArray(RichNodes.Node("paragraph", block.Title.Length > 0 ? RichNodes.Text(block.Title) : null)),
                    });
                }
            }
        }
        return document;
    }

    private static string? Prop(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
