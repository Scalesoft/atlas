using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// WP-20 (spec §6): a write the agent calls in a turn becomes a proposal with a server-built preview; only the owner's confirmation
/// runs the stored call, with only the chosen items, through WP-21's real <c>atlas_apply_labels</c> (one item = one <c>changes[i]</c>).
/// </summary>
public sealed class AssistantProposalTests
{
    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task Three_label_changes_become_a_card_and_the_decision_runs_only_the_chosen_stored_items()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantProposalTests));
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var (stranger, strangerId) = await f.UserAsync("stranger", level: null);
        var space = await f.SpaceAsync("IT");
        await f.GrantAsync(space, writerId, "Writer");
        var vpn = await PageAsync(f.Admin, space, "VPN", Rich(Paragraph("Postup.")), "Postup.");
        var wifi = await PageAsync(f.Admin, space, "Wi-Fi", Rich(Paragraph("Postup.")), "Postup.");
        var mail = await PageAsync(f.Admin, space, "Pošta", Rich(Paragraph("Postup.")), "Postup.");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{wifi.Id}/tags", new { names = new[] { "síť" }, expectedTagIds = Array.Empty<Guid>() }),
            HttpStatusCode.OK);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        await PolicyAsync(f, strangerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var history = "";
        fake.Script = messages =>
        {
            var question = messages.Last(message => message.Role == ChatRole.User).Text;
            if (messages[^1].Role == ChatRole.Tool)
            {
                return new ChatMessage(ChatRole.Assistant, "Navrhuji ikony pro tři stránky; zkontrolujte kartu a potvrďte ji.");
            }
            if (question.Contains("Personální"))
            {
                history = string.Join("\n", messages.Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text));
                return new ChatMessage(ChatRole.Assistant, "Rozumím.");
            }
            return Call(Changes(new { pageId = vpn.Id, icon = "book-open", color = "blue-3" },
                new { pageId = wifi.Id, addTags = new[] { "vpn" }, removeTags = new[] { "síť" } },
                new { pageId = mail.Id, icon = "star", color = "red-2", addTags = new[] { "pošta" } }));
        };

        var prepared = await PrepareAsync(writer, "Přidej ikonky stránkám v IT");
        var lines = await StreamAsync(writer, prepared["messageId"]!.GetValue<string>());
        Assert.Equal(["step", "step", "proposal", "message"], lines.Select(line => line["type"]!.GetValue<string>()));
        Assert.True(lines[1]["ok"]!.GetValue<bool>());
        var proposal = lines[2]["proposal"]!;
        var message = lines[3]["message"]!;
        Assert.Equal((proposal["id"]!.GetValue<string>(), "pending", "atlas_apply_labels"),
            (message["proposalId"]!.GetValue<string>(), proposal["state"]!.GetValue<string>(), proposal["tool"]!.GetValue<string>()));
        Assert.Equal("Navrhuji ikony pro tři stránky; zkontrolujte kartu a potvrďte ji.", message["sentences"]![0]!["text"]!.GetValue<string>());
        var items = proposal["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        // The preview is the server's: title and path read now, the current labels, the proposed ones; nothing ran yet.
        Assert.Equal((0, vpn.Id, "VPN", f.SpaceName(space) + " › VPN", "labels"), (items[0]!["index"]!.GetValue<int>(), items[0]!["pageId"]!.GetValue<string>(),
            items[0]!["title"]!.GetValue<string>(), items[0]!["path"]!.GetValue<string>(), items[0]!["kind"]!.GetValue<string>()));
        Same("""{"icon":null,"color":null,"tags":[]}""", items[0]!["before"]!);
        Same("""{"icon":"book-open","color":"blue-3","tags":[]}""", items[0]!["after"]!);
        Same("""{"icon":null,"color":null,"tags":["síť"]}""", items[1]!["before"]!);
        Same("""{"icon":null,"color":null,"tags":["vpn"]}""", items[1]!["after"]!);
        Assert.Null((await LabelsAsync(f, vpn.Id)).Decoration);
        // The tool result told the model the proposal waits; the reopened conversation and GET find the same card.
        var id = proposal["id"]!.GetValue<string>();
        Assert.Equal(proposal.ToJsonString(), (await AtlasHttpSite.Json(await writer.GetAsync($"/api/ai/proposals/{id}"), HttpStatusCode.OK)).ToJsonString());
        var reopened = await AtlasHttpSite.Json(await writer.GetAsync($"/api/ai/conversations/{prepared["conversationId"]!.GetValue<string>()}"), HttpStatusCode.OK);
        Assert.Equal(id, reopened["messages"]!.AsArray()[^1]!["proposalId"]!.GetValue<string>());

        // Somebody else's proposal does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/ai/proposals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(stranger, HttpMethod.Post, $"/api/ai/proposals/{id}/decision",
            new { approve = true, items = new[] { 0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/proposals/{id}/decision",
            new { approve = true, items = new[] { 0, 3 } })).StatusCode);

        // Items 0 and 2; whatever else the body carries is ignored: only the stored arguments of the chosen items run, once.
        var decided = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/proposals/{id}/decision", new
        {
            approve = true, items = new[] { 0, 2 }, tool = "atlas_publish",
            arguments = new { changes = new[] { new { pageId = wifi.Id, icon = "skull", color = "red-5" } } },
            changes = new[] { new { pageId = wifi.Id, icon = "skull", color = "red-5" } },
        }), HttpStatusCode.OK);
        Assert.Equal("applied", decided["state"]!.GetValue<string>());
        Assert.Equal(["applied", "skipped", "applied"], decided["items"]!.AsArray().Select(item => item!["status"]!.GetValue<string>()));
        Assert.Equal(("book-open/blue-3", ""), Show(await LabelsAsync(f, vpn.Id)));
        Assert.Equal(("star/red-2", "pošta"), Show(await LabelsAsync(f, mail.Id)));
        // The unchosen page and the body's foreign changes: untouched.
        Assert.Equal(("-", "síť"), Show(await LabelsAsync(f, wifi.Id)));
        Assert.Equal("proposal_decided", (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/proposals/{id}/decision",
            new { approve = true, items = new[] { 1 } }), HttpStatusCode.Conflict))["code"]!.GetValue<string>());
        Assert.Equal(("-", "síť"), Show(await LabelsAsync(f, wifi.Id)));
        // Audit: the proposal, the tool's write per page and the decision, each on the channel assistant without a token.
        Assert.Equal("assistant.proposal:pending,atlas_apply_labels:ok,atlas_apply_labels:ok,assistant.decision:applied", await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", ',' ORDER BY "Id") FROM "ApiActivity"
            WHERE "ActorId"=@writer::uuid AND "Channel"='assistant' AND "TokenId" IS NULL
            """, ("writer", writerId)));

        // The next turn knows what was applied and what was not.
        await TurnAsync(writer, "A teď stejně pro Personální", prepared["conversationId"]!.GetValue<string>());
        Assert.Contains("[Výsledek návrhu změn: provedeno.", history);
        Assert.Contains($"VPN (pageId {vpn.Id}): provedeno; navrženo: ikona book-open, barva blue-3", history);
        Assert.Contains($"Wi-Fi (pageId {wifi.Id}): uživatel nevybral, beze změny", history);
    }

    [PostgreSqlFact]
    public async Task A_proposal_is_refused_when_invalid_expired_superseded_or_without_the_tools_right_and_goes_with_its_conversation()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantProposalTests));
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("IT");
        await f.GrantAsync(space, writerId, "Writer");
        var vpn = await PageAsync(f.Admin, space, "VPN", Rich(Paragraph("Postup.")), "Postup.");
        var wifi = await PageAsync(f.Admin, space, "Wi-Fi", Rich(Paragraph("Postup.")), "Postup.");
        var closed = await PageAsync(f.Admin, await f.SpaceAsync("Vedení"), "Tajné", Rich(Paragraph("Tajné.")), "Tajné.");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{wifi.Id}/tags", new { names = new[] { "síť" }, expectedTagIds = Array.Empty<Guid>() }),
            HttpStatusCode.OK);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var results = new List<string>();
        fake.Script = messages =>
        {
            var question = messages.Last(message => message.Role == ChatRole.User).Text;
            if (messages[^1].Role == ChatRole.Tool)
            {
                results.Add(string.Join('\n', messages[^1].Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString())));
                return new ChatMessage(ChatRole.Assistant, "Hotovo.");
            }
            return question.Contains("neplatnou") ? Call(Changes(new { pageId = vpn.Id, icon = "neexistuje", color = "blue-3" }))
                : question.Contains("tajné") ? Call(Changes(new { pageId = closed.Id, addTags = new[] { "x" } }))
                : question.Contains("dvakrát") ? Call(Changes(new { pageId = vpn.Id, addTags = new[] { "a" } }), Changes(new { pageId = wifi.Id, addTags = new[] { "b" } }))
                : question.Contains("díky") ? new ChatMessage(ChatRole.Assistant, "Prosím.")
                : Call(Changes(new { pageId = vpn.Id, icon = "star", color = "red-2", addTags = new[] { "a" } },
                    new { pageId = wifi.Id, addTags = new[] { "vpn" }, expectedTagIds = Array.Empty<string>() }));
        };

        // A change the server would refuse, or a page the user may not read, never becomes a card: the model reads why.
        var invalid = await TurnAsync(writer, "Přidej neplatnou ikonu stránce VPN");
        Assert.Null(invalid.Proposal);
        Assert.True(results[^1].Contains("invalid_proposal"), results[^1]);
        var secret = await TurnAsync(writer, "Přidej tag na tajné stránky");
        Assert.Null(secret.Proposal);
        Assert.Contains("not_found", results[^1]);
        Assert.DoesNotContain("Tajné", results[^1]);
        // One proposal per turn: the second write call is refused.
        var twice = await TurnAsync(writer, "Přidej tagy dvakrát");
        Assert.Single(twice.Proposal!["items"]!.AsArray());
        Assert.Contains("proposal_pending", results[^1]);

        // A new message supersedes the open proposal.
        var superseded = twice.Proposal!["id"]!.GetValue<string>();
        await TurnAsync(writer, "díky", twice.ConversationId);
        Assert.Equal("superseded", (await AtlasHttpSite.Json(await writer.GetAsync($"/api/ai/proposals/{superseded}"), HttpStatusCode.OK))["state"]!.GetValue<string>());
        Assert.Equal("proposal_superseded", await DecideAsync(writer, superseded, HttpStatusCode.Conflict));

        // Expired after 15 minutes.
        var expiring = (await TurnAsync(writer, "Přidej tagy stránkám")).Proposal!["id"]!.GetValue<string>();
        await f.Site.ExecuteAsync("""UPDATE "AiToolProposal" SET "CreatedAt"=now()-interval '20 minutes',"ExpiresAt"=now()-interval '5 minutes' WHERE "Id"=@id::uuid""",
            ("id", expiring));
        Assert.Equal("expired", (await AtlasHttpSite.Json(await writer.GetAsync($"/api/ai/proposals/{expiring}"), HttpStatusCode.OK))["state"]!.GetValue<string>());
        Assert.Equal("proposal_expired", await DecideAsync(writer, expiring, HttpStatusCode.Conflict));

        // The tools right taken away between the proposal and the confirmation refuses it; nothing ran and it stays open.
        var open = await TurnAsync(writer, "Přidej tagy stránkám");
        var openId = open.Proposal!["id"]!.GetValue<string>();
        await PolicyAsync(f, writerId, tools: false);
        Assert.Equal(AiGate.ToolsForbiddenCode, await DecideAsync(writer, openId, HttpStatusCode.Forbidden));
        Assert.Equal("pending", (await AtlasHttpSite.Json(await writer.GetAsync($"/api/ai/proposals/{openId}"), HttpStatusCode.OK))["state"]!.GetValue<string>());
        Assert.Equal(("-", ""), Show(await LabelsAsync(f, vpn.Id)));

        // wifi's expectedTagIds is stale (it has "síť"): the all-or-nothing call saves nothing; the failing item maps back to its
        // original index, the other chosen one is rolled back.
        await PolicyAsync(f, writerId);
        var failed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/proposals/{openId}/decision",
            new { approve = true, items = new[] { 1, 0 } }), HttpStatusCode.OK);
        Assert.Equal("failed", failed["state"]!.GetValue<string>());
        Assert.Equal(["rolled_back", "tags_changed"], failed["items"]!.AsArray().Select(item => item!["code"]!.GetValue<string>()));
        Assert.Equal(("-", ""), Show(await LabelsAsync(f, vpn.Id)));
        Assert.Equal(("-", "síť"), Show(await LabelsAsync(f, wifi.Id)));

        // Deleting the conversation deletes its proposals.
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "AiToolProposal" WHERE "ConversationId"=@id::uuid""", ("id", open.ConversationId)));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Delete, $"/api/ai/conversations/{open.ConversationId}")).StatusCode);
        Assert.Equal(0L, await f.Site.ScalarAsync("""SELECT count(*) FROM "AiToolProposal" WHERE "ConversationId"=@id::uuid""", ("id", open.ConversationId)));
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/ai/proposals/{openId}")).StatusCode);
    }

    private static void Same(string expected, JsonNode actual) => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());

    private static async Task<(JsonNode? Decoration, JsonNode Tags)> LabelsAsync(McpSite f, string pageId)
        => ((await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{pageId}/decoration"), HttpStatusCode.OK))["decoration"],
            (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{pageId}/tags"), HttpStatusCode.OK))["tags"]!);

    /// <summary>"icon/color" or "-", and the tag names joined.</summary>
    private static (string Decoration, string Tags) Show((JsonNode? Decoration, JsonNode Tags) labels)
        => (labels.Decoration is { } decoration ? decoration["icon"]!.GetValue<string>() + "/" + decoration["color"]!.GetValue<string>() : "-",
            string.Join(",", labels.Tags.AsArray().Select(tag => tag!["name"]!.GetValue<string>())));

    private static Dictionary<string, object?> Changes(params object[] changes) => new() { ["changes"] = JsonSerializer.SerializeToElement(changes) };

    private static ChatMessage Call(params Dictionary<string, object?>[] calls)
        => new(ChatRole.Assistant, [.. calls.Select((arguments, index) => (AIContent)new FunctionCallContent("labels" + index, "atlas_apply_labels", arguments))]);

    internal static async Task<(string ConversationId, JsonNode? Proposal)> TurnAsync(HttpClient client, string question, string? conversationId = null)
    {
        var prepared = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post,
            conversationId is null ? "/api/ai/conversations" : $"/api/ai/conversations/{conversationId}/messages",
            new { origin = "assistant", question, scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.OK);
        var lines = await StreamAsync(client, prepared["messageId"]!.GetValue<string>());
        Assert.Equal("answered", lines[^1]["message"]!["state"]!.GetValue<string>());
        return (prepared["conversationId"]!.GetValue<string>(), lines.SingleOrDefault(line => line["type"]!.GetValue<string>() == "proposal")?["proposal"]);
    }

    private static async Task<string> DecideAsync(HttpClient client, string id, HttpStatusCode status)
        => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/ai/proposals/{id}/decision",
            new { approve = true, items = new[] { 0 } }), status))["code"]!.GetValue<string>();

    private static async Task<JsonNode> PrepareAsync(HttpClient client, string question)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question, scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.OK);

    internal static async Task<JsonNode[]> StreamAsync(HttpClient client, string messageId)
    {
        var csrf = await AtlasHttpSite.Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/ai/messages/{messageId}/complete");
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
    }

    internal static async Task PolicyAsync(McpSite f, string userId, bool tools = true)
    {
        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/admin/users/{userId}/ai-policy"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/admin/users/{userId}/ai-policy",
            new { enabled = true, toolsEnabled = tools, searchAnswerDailyLimit = (int?)null, chatAnswerDailyLimit = (int?)null, pageSummaryDailyLimit = (int?)null,
                agentDailyLimit = (int?)null, expectedVersion = current["version"]?.GetValue<string>() }), HttpStatusCode.OK);
    }
}
