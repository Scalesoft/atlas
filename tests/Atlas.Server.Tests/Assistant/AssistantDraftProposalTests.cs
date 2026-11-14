using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// WP-31 (spec §7.2): a draft write of the agent is one card with a server-built preview (changed sections, placeholders as
/// "komponenta"), and only the confirmation runs the real MCP draft tool as the user on the channel assistant. What the tool refuses
/// at that moment (a dropped placeholder, the user's own unsaved live edits) fails the card and saves nothing.
/// </summary>
public sealed class AssistantDraftProposalTests
{
    // A page reference: an inline component that stays a placeholder (a contact is Markdown of its own, a-contact).
    private const string Contact = """{"type":"pageReference","attrs":{"id":"5e940f4d-55c2-4527-addd-10f1d68d742c","title":"Jan Kontaktní","url":"#/pages/5e940f4d-55c2-4527-addd-10f1d68d742c"}}""";

    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task A_section_update_keeps_the_contact_and_is_saved_as_the_user_on_the_assistant_channel()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var page = await PageAsync(f.Admin, space, "Kontakty", ContactPage(), "Volejte.");
        await ReadyAsync(f, writerId);
        Script(f, _ => Call("atlas_update_section", new()
        {
            ["pageId"] = page.Id, ["expectedVersion"] = page.Version, ["headingId"] = "kontakty", ["markdown"] = "## Kontakty\n\nV noci volejte ⟦c:1⟧.\n",
        }));

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Uprav sekci Kontakty na stránce Kontakty")).Proposal!;
        Assert.Equal("atlas_update_section", proposal["tool"]!.GetValue<string>());
        var item = proposal["items"]!.AsArray().Single()!;
        Assert.Equal((page.Id, "Kontakty", f.SpaceName(space) + " › Kontakty", "content"), (item["pageId"]!.GetValue<string>(), item["title"]!.GetValue<string>(),
            item["path"]!.GetValue<string>(), item["kind"]!.GetValue<string>()));
        // Only the changed section, from the working version as the server reads it; the component is a label, never its node.
        Assert.Equal(["Kontakty"], item["text"]!["sections"]!.AsArray().Select(x => x!.GetValue<string>()));
        var before = item["text"]!["before"]!.GetValue<string>();
        var after = item["text"]!["after"]!.GetValue<string>();
        Assert.StartsWith("## Kontakty\n\nVolejte ⟦komponenta: ", before);
        Assert.StartsWith("## Kontakty\n\nV noci volejte ⟦komponenta: ", after);
        Assert.DoesNotContain("Jiné", before + after);
        Assert.DoesNotContain("⟦c:", before + after);
        Assert.Equal(page.Version, await VersionAsync(f, page.Id));

        var decided = await DecideAsync(writer, proposal);
        Assert.Equal(("applied", "applied"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["status"]!.GetValue<string>()));
        var stored = JsonNode.Parse((string)(await CurrentAsync(f, page.Id, "ContentJson"))!)!["document"]!["content"]![1]!["content"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Contact), stored.Single(x => x!["type"]!.GetValue<string>() == "pageReference")));
        Assert.Equal("V noci volejte ", stored[0]!["text"]!.GetValue<string>());
        Assert.Equal(("assistant", writerId), ((string)(await CurrentAsync(f, page.Id, "Channel"))!, (await CurrentAsync(f, page.Id, "AuthorId"))!.ToString()));
        Assert.Equal(page.Version + 1, await VersionAsync(f, page.Id));
    }

    [PostgreSqlFact]
    public async Task Reformatting_like_another_page_is_one_card_and_a_dropped_placeholder_fails_with_nothing_saved()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var model = await PageAsync(f.Admin, space, "Pravidla hesel", Rich(Heading(2, "delka", "Délka hesla"), Paragraph("Nejméně 14 znaků."),
            Heading(2, "sdilena", "Sdílená hesla"), Paragraph("Do trezoru.")), "Nejméně 14 znaků.");
        var target = await PageAsync(f.Admin, space, "Kontakty", ContactPage(), "Volejte.");
        // The model reads X for its structure, then the target, then proposes the whole page once, but loses the contact.
        await ReadyAsync(f, writerId);
        Script(f, messages => messages.Count(message => message.Role == ChatRole.Tool) switch
        {
            0 => Call("atlas_get_page", new() { ["pageId"] = model.Id }),
            1 => Call("atlas_get_page", new() { ["pageId"] = target.Id }),
            _ => Call("atlas_replace_page", new()
            {
                ["pageId"] = target.Id, ["expectedVersion"] = target.Version, ["markdown"] = "## Komu volat\n\nVolejte dispečink.\n\n## Jiné\n\nBeze změny.\n",
            }),
        });

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Přeformátuj stránku Kontakty podle stránky Pravidla hesel")).Proposal!;
        Assert.Equal("atlas_replace_page", proposal["tool"]!.GetValue<string>());
        var item = proposal["items"]!.AsArray().Single()!;
        Assert.Equal(["Komu volat", "Kontakty"], item["text"]!["sections"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("⟦komponenta: ", item["text"]!["before"]!.GetValue<string>());
        Assert.DoesNotContain("⟦komponenta", item["text"]!["after"]!.GetValue<string>());

        var decided = await DecideAsync(writer, proposal);
        Assert.Equal(("failed", "components_removed"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["code"]!.GetValue<string>()));
        Assert.Equal(target.Version, await VersionAsync(f, target.Id));
        Assert.Contains("pageReference", (string)(await CurrentAsync(f, target.Id, "ContentJson"))!);
    }

    [PostgreSqlFact]
    public async Task The_users_own_unsaved_live_edits_fail_the_card_and_survive()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var page = await PageAsync(f.Admin, space, "Moje", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var document = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"), HttpStatusCode.OK))
            ["documentId"]!.GetValue<string>();
        await new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>())
            .AppendUpdateAsync(document, Guid.NewGuid(), writerId, DateTimeOffset.UtcNow, new byte[] { 1 }, default);
        await ReadyAsync(f, writerId);
        Script(f, _ => Call("atlas_update_section", new()
        {
            ["pageId"] = page.Id, ["expectedVersion"] = page.Version, ["headingId"] = "a", ["markdown"] = "## A\n\nZměna od asistenta.\n",
        }));

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Uprav sekci A na stránce Moje")).Proposal!;
        var decided = await DecideAsync(writer, proposal);

        Assert.Equal(("failed", "collaboration_unsaved"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["code"]!.GetValue<string>()));
        Assert.Equal(page.Version, await VersionAsync(f, page.Id));
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "CollaborationUpdate" WHERE "PageId"=@page""", ("page", Guid.Parse(page.Id))));
    }

    [PostgreSqlFact]
    public async Task A_new_draft_and_a_rename_are_previewed_and_applied_and_what_the_model_can_fix_is_a_tool_error()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var vpn = await PageAsync(f.Admin, space, "VPN", Rich(Heading(2, "postup", "Postup"), Paragraph("Zapněte tunel.")), "Zapněte tunel.");
        var results = new List<string>();
        await ReadyAsync(f, writerId);
        Script(f, messages =>
        {
            var question = messages.Last(message => message.Role == ChatRole.User).Text;
            if (messages[^1].Role == ChatRole.Tool)
            {
                results.Add(string.Join('\n', messages[^1].Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString())));
                return new ChatMessage(ChatRole.Assistant, "Hotovo.");
            }
            return question.Contains("koncept") ? Call("atlas_create_draft", new()
                {
                    ["spaceId"] = space, ["parentId"] = vpn.Id, ["title"] = "Čipové karty", ["markdown"] = "## Postup\n\nPřiložte kartu ke čtečce.\n",
                })
                : question.Contains("Přejmenuj") ? Call("atlas_rename_page", new() { ["pageId"] = vpn.Id, ["expectedVersion"] = vpn.Version, ["title"] = "Připojení VPN" })
                : question.Contains("nadpisu") ? Call("atlas_update_section", new()
                {
                    ["pageId"] = vpn.Id, ["expectedVersion"] = vpn.Version, ["headingId"] = "postup", ["markdown"] = "Bez nadpisu.",
                })
                : Call("atlas_update_section", new()
                {
                    ["pageId"] = vpn.Id, ["expectedVersion"] = vpn.Version + 5, ["headingId"] = "postup", ["markdown"] = "## Postup\n\nNově.\n",
                });
        });

        // A section without its heading and a stale version never become a card: the model reads the tool error and can fix it.
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Uprav sekci Postup bez nadpisu")).Proposal);
        Assert.Contains("invalid_markdown", results[^1]);
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Uprav sekci Postup na stránce VPN")).Proposal);
        Assert.Contains("version_conflict", results[^1]);

        var draft = (await AssistantProposalTests.TurnAsync(writer, "Napiš nový koncept o čipových kartách k návodu VPN")).Proposal!;
        var item = draft["items"]!.AsArray().Single()!;
        Assert.Equal((Guid.Empty.ToString(), "Čipové karty", f.SpaceName(space) + " › VPN › Čipové karty", "draft"), (item["pageId"]!.GetValue<string>(),
            item["title"]!.GetValue<string>(), item["path"]!.GetValue<string>(), item["kind"]!.GetValue<string>()));
        Assert.Equal(["Postup"], item["text"]!["sections"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("## Postup\n\nPřiložte kartu ke čtečce.", item["text"]!["after"]!.GetValue<string>());
        var created = (await DecideAsync(writer, draft))["items"]![0]!;
        Assert.Equal("applied", created["status"]!.GetValue<string>());
        // The card now names the new page, so "Otevřít v editoru" opens it; a draft, never published, by the user on the assistant channel.
        var newPage = created["pageId"]!.GetValue<string>();
        Assert.NotEqual(Guid.Empty.ToString(), newPage);
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("""SELECT "PublishedRevisionId" FROM "Page" WHERE "Id"=@page""", ("page", Guid.Parse(newPage))) ?? DBNull.Value);
        Assert.Equal(("assistant", writerId), ((string)(await CurrentAsync(f, newPage, "Channel"))!, (await CurrentAsync(f, newPage, "AuthorId"))!.ToString()));

        var rename = (await AssistantProposalTests.TurnAsync(writer, "Přejmenuj stránku VPN na Připojení VPN")).Proposal!;
        var renamed = rename["items"]!.AsArray().Single()!;
        Assert.Equal(("rename", "VPN", "Připojení VPN"), (renamed["kind"]!.GetValue<string>(), renamed["text"]!["before"]!.GetValue<string>(),
            renamed["text"]!["after"]!.GetValue<string>()));
        Assert.Equal("applied", (await DecideAsync(writer, rename))["state"]!.GetValue<string>());
        Assert.Equal("Připojení VPN", (string)(await CurrentAsync(f, vpn.Id, "Title"))!);
    }

    private static async Task<(McpSite Site, HttpClient Writer, string WriterId, string Space)> SiteAsync()
    {
        var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantDraftProposalTests));
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("IT");
        await f.GrantAsync(space, writerId, "Writer");
        return (f, writer, writerId, space);
    }

    /// <summary>After the pages exist: the index needs one to build.</summary>
    private static async Task ReadyAsync(McpSite f, string writerId)
    {
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await AssistantProposalTests.PolicyAsync(f, writerId);
    }

    private static string ContactPage() => Rich(Heading(2, "kontakty", "Kontakty"),
        """{"type":"paragraph","content":[{"type":"text","text":"Volejte "},""" + Contact + "]}", Heading(2, "jine", "Jiné"), Paragraph("Beze změny."));

    /// <summary>The fake model: the script's call, or a closing sentence once a write was answered (the turn's last tool result).</summary>
    private static void Script(McpSite f, Func<IReadOnlyList<ChatMessage>, ChatMessage> script)
        => f.Site.Services.GetRequiredService<FakeAiClientFactory>().Script = messages =>
            messages[^1].Role == ChatRole.Tool && messages[^1].Contents.OfType<FunctionResultContent>().Any(result => result.Result?.ToString()?.Contains("proposal") == true)
                ? new ChatMessage(ChatRole.Assistant, "Návrh je na kartě, zkontrolujte ho a potvrďte.")
                : script(messages);

    private static ChatMessage Call(string tool, Dictionary<string, object?> arguments)
        => new(ChatRole.Assistant, [new FunctionCallContent("call-" + tool, tool, arguments)]);

    private static async Task<JsonNode> DecideAsync(HttpClient client, JsonNode proposal)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/ai/proposals/{proposal["id"]!.GetValue<string>()}/decision",
            new { approve = true, items = new[] { 0 } }), HttpStatusCode.OK);

    private static async Task<int> VersionAsync(McpSite f, string pageId)
        => (int)(await f.Site.ScalarAsync("""SELECT "Version" FROM "Page" WHERE "Id"=@page""", ("page", Guid.Parse(pageId))))!;

    private static Task<object?> CurrentAsync(McpSite f, string pageId, string column)
        => f.Site.ScalarAsync($"""SELECT "{column}" FROM "PageRevision" WHERE "PageId"=@page AND "ActiveTo" IS NULL""", ("page", Guid.Parse(pageId)));
}
