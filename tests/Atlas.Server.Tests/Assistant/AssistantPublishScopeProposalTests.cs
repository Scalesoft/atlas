using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// WP-40 and WP-41 (spec D4 (3), §6.3, D18, D19): a move or a publication the agent calls is one card with a server-built preview
/// (path before → after and the subpages that move along; the facts of the MCP confirmation page), and only the confirmation runs the
/// real MCP tool as the user on the channel assistant. The content rights of the shared handlers decide; there is no API level.
/// atlas_publish on the channel assistant publishes only under the approved-proposal marker of the decision executor.
/// </summary>
public sealed class AssistantPublishScopeProposalTests
{
    [PostgreSqlFact]
    public async Task A_move_under_a_page_previews_the_paths_and_subpages_and_moves_the_page_on_the_assistant_channel()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var servers = await PageAsync(f.Admin, space, "Servery", Rich(Paragraph("Přehled.")), "Přehled.");
        var printers = await PageAsync(f.Admin, space, "Tiskárny", Rich(Paragraph("Patra.")), "Patra.");
        var scanners = await PageAsync(f.Admin, space, "Skenery", Rich(Paragraph("Sken.")), "Sken.", parentId: printers.Id);
        var tree = await TreeVersionAsync(f, space);
        var results = new List<string>();
        await ReadyAsync(f, writerId);
        Script(f, results, question => Call("atlas_move_page", new()
        {
            ["pageId"] = question.Contains("nikam") ? "Tiskárny" : printers.Id, ["expectedTreeVersion"] = question.Contains("znovu") ? tree + 5 : tree,
            ["parentId"] = servers.Id, ["beforePageId"] = question.Contains("před") ? scanners.Id : null,
        }));

        // A stale tree never becomes a card: the model reads the conflict and re-reads the tree.
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Přesuň znovu Tiskárny pod stránku Servery")).Proposal);
        Assert.Contains("version_conflict", results[^1]);
        // One move names its own arguments, never moves[0] or "Přesun 1" of a menu.
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Přesuň Tiskárny nikam")).Proposal);
        Assert.Contains(" pageId musí být UUID.", Regex.Unescape(results[^1]));
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Přesuň Tiskárny pod Servery před Skenery")).Proposal);
        Assert.Contains("beforePageId musí být jiná stránka pod tímtéž rodičem.", Regex.Unescape(results[^1]));
        Assert.All(results, result => Assert.DoesNotContain("moves", result));
        Assert.All(results, result => Assert.DoesNotContain("Přesun 1", Regex.Unescape(result)));

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Přesuň stránku Tiskárny pod stránku Servery")).Proposal!;
        Assert.Equal("atlas_move_page", proposal["tool"]!.GetValue<string>());
        var item = proposal["items"]!.AsArray().Single()!;
        Assert.Equal((printers.Id, "Tiskárny", f.SpaceName(space) + " › Tiskárny", "move"), (item["pageId"]!.GetValue<string>(), item["title"]!.GetValue<string>(),
            item["path"]!.GetValue<string>(), item["kind"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"before":"{{f.SpaceName(space)}} › Tiskárny","after":"{{f.SpaceName(space)}} › Servery › Tiskárny","subpages":1,"otherSpace":false}"""),
            item["move"]), item["move"]!.ToJsonString());
        Assert.Null(await ParentAsync(f, printers.Id));

        var decided = await DecideAsync(writer, proposal);
        Assert.Equal(("applied", "applied"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["status"]!.GetValue<string>()));
        Assert.Equal(servers.Id, await ParentAsync(f, printers.Id));
        Assert.Equal("assistant.proposal:pending,atlas_move_page:ok,assistant.decision:applied", await AuditAsync(f, writerId));
    }

    [PostgreSqlFact]
    public async Task A_menu_is_one_card_with_a_row_per_move_and_the_chosen_moves_run_together()
    {
        // Isolated: switches the task assistant.arrange-pages (AiFeature, one per installation).
        var (f, writer, writerId, space) = await SiteAsync(isolated: true);
        await using var _ = f;
        var servers = await PageAsync(f.Admin, space, "Servery", Rich(Paragraph("Přehled.")), "Přehled.");
        var printers = await PageAsync(f.Admin, space, "Tiskárny", Rich(Paragraph("Patra.")), "Patra.");
        var scanners = await PageAsync(f.Admin, space, "Skenery", Rich(Paragraph("Sken.")), "Sken.", parentId: printers.Id);
        var wifi = await PageAsync(f.Admin, space, "Wi-Fi", Rich(Paragraph("Síť.")), "Síť.");
        var tree = await TreeVersionAsync(f, space);
        var results = new List<string>();
        await ReadyAsync(f, writerId);
        Script(f, results, question => Call("atlas_arrange_pages", new()
        {
            ["expectedTreeVersion"] = tree,
            ["moves"] = new object[]
            {
                new Dictionary<string, object?> { ["pageId"] = printers.Id, ["parentId"] = servers.Id },
                new Dictionary<string, object?> { ["pageId"] = wifi.Id, ["parentId"] = servers.Id, ["beforePageId"] = question.Contains("špatně") ? scanners.Id : printers.Id },
            },
        }));

        // A sibling that is not under the new parent is a tool error naming the move, never a card.
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Poskládej levé menu prostoru IT špatně")).Proposal);
        Assert.Contains("beforePageId", results[^1]);
        Assert.Contains("invalid_argument", results[^1]);

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Poskládej levé menu prostoru IT")).Proposal!;
        Assert.Equal("atlas_arrange_pages", proposal["tool"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            [{"before":"{{f.SpaceName(space)}} › Tiskárny","after":"{{f.SpaceName(space)}} › Servery › Tiskárny","subpages":1,"otherSpace":false},
             {"before":"{{f.SpaceName(space)}} › Wi-Fi","after":"{{f.SpaceName(space)}} › Servery › Wi-Fi (před Tiskárny)","subpages":0,"otherSpace":false}]
            """), new JsonArray(proposal["items"]!.AsArray().Select(item => item!["move"]!.DeepClone()).ToArray())));
        Assert.Null(await ParentAsync(f, printers.Id));

        // Switched off by the admin meanwhile, the open card no longer applies; switched on again, it does.
        await ArrangeAsync(f, false);
        var refused = await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/proposals/{proposal["id"]!.GetValue<string>()}/decision",
            new { approve = true, items = new[] { 0, 1 } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        await ArrangeAsync(f, true);

        var decided = await DecideAsync(writer, proposal, 0, 1);
        Assert.Equal(["applied", "applied"], decided["items"]!.AsArray().Select(item => item!["status"]!.GetValue<string>()));
        Assert.Equal((servers.Id, servers.Id), (await ParentAsync(f, printers.Id), await ParentAsync(f, wifi.Id)));
        Assert.Equal(printers.Id, await ParentAsync(f, scanners.Id));
        Assert.Equal($"{wifi.Id},{printers.Id}", await f.Site.ScalarAsync("""
            SELECT string_agg("Id"::text, ',' ORDER BY "SortOrder") FROM "Page" WHERE "ParentId"=@parent
            """, ("parent", Guid.Parse(servers.Id))));
        Assert.Equal("assistant.proposal:pending,atlas_arrange_pages:ok,assistant.decision:applied", await AuditAsync(f, writerId));
    }

    [PostgreSqlFact]
    public async Task A_move_left_out_of_a_menu_can_fail_the_one_that_needed_it_and_nothing_moves()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var servers = await PageAsync(f.Admin, space, "Servery", Rich(Paragraph("Přehled.")), "Přehled.");
        var printers = await PageAsync(f.Admin, space, "Tiskárny", Rich(Paragraph("Patra.")), "Patra.");
        var wifi = await PageAsync(f.Admin, space, "Wi-Fi", Rich(Paragraph("Síť.")), "Síť.");
        var tree = await TreeVersionAsync(f, space);
        await ReadyAsync(f, writerId);
        Script(f, [], _ => Call("atlas_arrange_pages", new()
        {
            ["expectedTreeVersion"] = tree,
            ["moves"] = new object[]
            {
                new Dictionary<string, object?> { ["pageId"] = printers.Id, ["parentId"] = servers.Id },
                new Dictionary<string, object?> { ["pageId"] = wifi.Id, ["parentId"] = servers.Id, ["beforePageId"] = printers.Id },
            },
        }));
        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Poskládej levé menu prostoru IT")).Proposal!;

        // Only the second row: Tiskárny stay at the root, so "before Tiskárny" under Servery cannot be done; that row fails, the other is skipped.
        var decided = await DecideAsync(writer, proposal, 1);
        Assert.Equal("failed", decided["state"]!.GetValue<string>());
        Assert.Equal(["skipped", "failed"], decided["items"]!.AsArray().Select(item => item!["status"]!.GetValue<string>()));
        Assert.Null(await ParentAsync(f, wifi.Id));
    }

    [PostgreSqlFact]
    public async Task A_move_into_a_space_the_user_may_only_read_fails_with_the_handlers_code_and_moves_nothing()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var board = await f.SpaceAsync("Vedení");
        await f.GrantAsync(board, writerId, "Reader");
        await PageAsync(f.Admin, board, "Strategie", Rich(Paragraph("Cíle.")), "Cíle.");
        var printers = await PageAsync(f.Admin, space, "Tiskárny", Rich(Paragraph("Patra.")), "Patra.");
        var (source, target) = (await TreeVersionAsync(f, space), await TreeVersionAsync(f, board));
        await ReadyAsync(f, writerId);
        Script(f, [], _ => Call("atlas_move_page_to_space", new()
        {
            ["pageId"] = printers.Id, ["targetSpaceId"] = board, ["expectedSourceTreeVersion"] = source, ["expectedTargetTreeVersion"] = target,
            ["expectedVersion"] = printers.Version, ["includeSubtree"] = true, ["acknowledgeAccessChange"] = true,
        }));

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Přesuň stránku Tiskárny do prostoru Vedení")).Proposal!;
        var item = proposal["items"]!.AsArray().Single()!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"before":"{{f.SpaceName(space)}} › Tiskárny","after":"{{f.SpaceName(board)}} › Tiskárny","subpages":0,"otherSpace":true}"""),
            item["move"]), item["move"]!.ToJsonString());

        var decided = await DecideAsync(writer, proposal);
        // The shared move requires Writer in the target; its reader knows the space and gets forbidden (review S3; with Writer it applies).
        Assert.Equal(("failed", "forbidden"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["code"]!.GetValue<string>()));
        Assert.Equal(space, (await f.Site.ScalarAsync("""SELECT "SpaceId" FROM "Page" WHERE "Id"=@page""", ("page", Guid.Parse(printers.Id))))!.ToString());
    }

    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task A_publication_is_a_card_with_the_check_and_only_the_confirmation_publishes_it_on_the_assistant_channel()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var page = await EditedPageAsync(writer, space);
        await ReadyAsync(f, writerId);
        Script(f, [], _ => Call("atlas_publish", new() { ["pageId"] = page.Id, ["expectedVersion"] = page.Version, ["changeSummary"] = "Doplněn odkaz" }));

        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Publikuj stránku VPN")).Proposal!;
        Assert.Equal("atlas_publish", proposal["tool"]!.GetValue<string>());
        var item = proposal["items"]!.AsArray().Single()!;
        Assert.Equal((page.Id, "VPN", f.SpaceName(space) + " › VPN", "publish"), (item["pageId"]!.GetValue<string>(), item["title"]!.GetValue<string>(),
            item["path"]!.GetValue<string>(), item["kind"]!.GetValue<string>()));
        var publish = item["publish"]!;
        Assert.Equal((page.Version, page.Version - 1, 1, "Doplněn odkaz"), (publish["version"]!.GetValue<int>(), publish["publishedVersion"]!.GetValue<int>(),
            publish["changedSections"]!.GetValue<int>(), publish["changeSummary"]!.GetValue<string>()));
        Assert.Equal("unavailable", publish["findings"]!["links"]!.AsArray().Single()!["state"]!.GetValue<string>());
        // The turn only proposed: still the first publication.
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));

        var decided = await DecideAsync(writer, proposal);
        Assert.Equal(("applied", "applied"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["status"]!.GetValue<string>()));
        Assert.Equal("assistant|" + writerId + "|Doplněn odkaz", await f.Site.ScalarAsync("""
            SELECT "Channel" || '|' || "PublisherId" || '|' || "ChangeSummary" FROM "PagePublication" WHERE "PageId"=@page AND "ActiveTo" IS NULL AND "ApiTokenId" IS NULL
            """, ("page", Guid.Parse(page.Id))));
        Assert.Equal(2L, await PublicationsAsync(f, page.Id));
        Assert.Equal("assistant.proposal:pending,atlas_publish:ok,assistant.decision:applied", await AuditAsync(f, writerId));
    }

    /// <summary>
    /// The never-published draft of its author (live evaluation on Gateway): read only in its working version, it is no citable published
    /// source, so it neither becomes a dependency nor withholds the answer; the steps are stored and the publication reaches the card.
    /// </summary>
    [PostgreSqlFact]
    public async Task An_editor_asking_about_a_never_published_draft_is_answered_and_its_publication_reaches_the_card()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        await PageAsync(f.Admin, space, "VPN", Rich(Paragraph("Postup.")), "Postup.");
        var draft = await PageAsync(writer, space, "Wi-Fi pro hosty", Rich(Paragraph("Síť Hosté.")), "Síť Hosté.", publish: false);
        await ReadyAsync(f, writerId);
        f.Site.Services.GetRequiredService<FakeAiClientFactory>().Script = messages =>
        {
            if (messages[^1].Role == ChatRole.Tool)
            {
                return new ChatMessage(ChatRole.Assistant, "Konceptu Wi-Fi pro hosty chybí heslo sítě.");
            }
            var read = new FunctionCallContent("read", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = draft.Id, ["version"] = "working" });
            return new ChatMessage(ChatRole.Assistant, messages.Last(message => message.Role == ChatRole.User).Text.StartsWith("Publikuj")
                ? [read, new FunctionCallContent("publish", "atlas_publish", new Dictionary<string, object?> { ["pageId"] = draft.Id, ["expectedVersion"] = draft.Version })]
                : [read]);
        };

        var (conversation, none) = await AssistantProposalTests.TurnAsync(writer, "Přečti koncept Wi-Fi pro hosty a řekni, co v něm chybí.");
        Assert.Null(none);
        Assert.Equal(0L, await f.Site.ScalarAsync("""
            SELECT count(*) FROM "AiSourceDependency" d JOIN "AiMessage" m ON m."Id"=d."MessageId" WHERE m."ConversationId"=@id::uuid
            """, ("id", conversation)));
        Assert.Contains("atlas_get_page", (string)(await f.Site.ScalarAsync("""
            SELECT CAST("Steps" AS text) FROM "AiMessage" WHERE "ConversationId"=@id::uuid AND "State"='answered'
            """, ("id", conversation)))!);

        // The next turn of the same conversation: its history has no draft dependency either, and the proposal is kept.
        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Publikuj koncept Wi-Fi pro hosty.", conversation)).Proposal!;
        Assert.Equal("atlas_publish", proposal["tool"]!.GetValue<string>());
        Assert.Equal(draft.Id, proposal["items"]![0]!["pageId"]!.GetValue<string>());
        Assert.Equal(0L, await PublicationsAsync(f, draft.Id));
        var decided = await DecideAsync(writer, proposal);
        Assert.Equal("applied", decided["state"]!.GetValue<string>());
        Assert.Equal(1L, await PublicationsAsync(f, draft.Id));
    }

    [PostgreSqlFact]
    public async Task The_assistant_never_publishes_without_the_approved_proposal_marker()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var page = await EditedPageAsync(writer, space);
        await using (var scope = f.Site.Services.CreateAsyncScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, writerId)], "Cookies")),
            };
            // The assistant's own turn: channel assistant, but no confirmed proposal.
            context.Items[HttpRequestChannel.AssistantMarker] = true;
            accessor.HttpContext = context;
            try
            {
                var refused = await Assert.ThrowsAsync<ApiException>(() => scope.ServiceProvider.GetRequiredService<IMessageBus>()
                    .InvokeAsync<McpPublishOutcome>(new PublishMcpApproved(Guid.Parse(page.Id), page.Version, null)));
                Assert.Equal((403, "publish_confirmation_required"), (refused.Status, refused.Code));
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));
    }

    [PostgreSqlFact]
    public async Task The_users_own_unsaved_live_edits_refuse_the_publication_at_the_proposal_and_at_the_confirmation()
    {
        var (f, writer, writerId, space) = await SiteAsync();
        await using var _ = f;
        var page = await EditedPageAsync(writer, space);
        var results = new List<string>();
        await ReadyAsync(f, writerId);
        Script(f, results, _ => Call("atlas_publish", new() { ["pageId"] = page.Id, ["expectedVersion"] = page.Version }));
        var proposal = (await AssistantProposalTests.TurnAsync(writer, "Publikuj stránku VPN")).Proposal!;

        var document = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"), HttpStatusCode.OK))
            ["documentId"]!.GetValue<string>();
        await new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>())
            .AppendUpdateAsync(document, Guid.NewGuid(), writerId, DateTimeOffset.UtcNow, new byte[] { 1 }, default);

        var decided = await DecideAsync(writer, proposal);
        Assert.Equal(("failed", "collaboration_unsaved"), (decided["state"]!.GetValue<string>(), decided["items"]![0]!["code"]!.GetValue<string>()));
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));
        // A new request while the edits are unsaved never becomes a card.
        Assert.Null((await AssistantProposalTests.TurnAsync(writer, "Publikuj stránku VPN")).Proposal);
        Assert.Contains("collaboration_unsaved", results[^1]);
    }

    private static async Task<(McpSite Site, HttpClient Writer, string WriterId, string Space)> SiteAsync(bool isolated = false)
    {
        var f = isolated ? await McpSite.CreateAsync(enabled: false, isolated: true) : await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantPublishScopeProposalTests));
        // No API level (D19): the content rights decide.
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

    /// <summary>A published page "VPN" with a newer working version: one section changed and a link to a page that does not exist.</summary>
    private static async Task<(string Id, int Version)> EditedPageAsync(HttpClient author, string space)
    {
        var page = await PageAsync(author, space, "VPN", Rich(Heading(2, "a", "A"), Paragraph("Publikováno.")), "Publikováno.");
        var link = """{"type":"paragraph","content":[{"type":"pageReference","attrs":{"id":""" + JsonSerializer.Serialize(Guid.NewGuid().ToString()) +
            ""","title":"Zmizelá","url":"#/pages/zmizela"}}]}""";
        return (page.Id, await SaveAsync(author, page.Id, page.Version, "VPN", Rich(Heading(2, "a", "A"), Paragraph("Upraveno."), link), "Upraveno."));
    }

    /// <summary>The fake model: the script's call for the question, then a closing sentence after any tool result (noted in <paramref name="results"/>).</summary>
    private static void Script(McpSite f, List<string> results, Func<string, ChatMessage> script)
        => f.Site.Services.GetRequiredService<FakeAiClientFactory>().Script = messages =>
        {
            if (messages[^1].Role == ChatRole.Tool)
            {
                results.Add(string.Join('\n', messages[^1].Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString())));
                return new ChatMessage(ChatRole.Assistant, "Hotovo.");
            }
            return script(messages.Last(message => message.Role == ChatRole.User).Text);
        };

    private static ChatMessage Call(string tool, Dictionary<string, object?> arguments)
        => new(ChatRole.Assistant, [new FunctionCallContent("call-" + tool, tool, arguments)]);

    private static async Task<JsonNode> DecideAsync(HttpClient client, JsonNode proposal, params int[] items)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/ai/proposals/{proposal["id"]!.GetValue<string>()}/decision",
            new { approve = true, items = items.Length == 0 ? new[] { 0 } : items }), HttpStatusCode.OK);

    private static async Task ArrangeAsync(McpSite f, bool enabled)
    {
        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/ai/features"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/features", new
        {
            items = new[] { new { task = "assistant.arrange-pages", enabled, cloudWorkingText = (bool?)null, dailyLimit = (int?)null } },
            expectedVersion = current["version"]?.GetValue<string>(),
        }), HttpStatusCode.OK);
    }

    private static async Task<long> TreeVersionAsync(McpSite f, string space)
        => (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/tree"), HttpStatusCode.OK))["version"]!.GetValue<long>();

    private static async Task<string?> ParentAsync(McpSite f, string pageId)
        => (await f.Site.ScalarAsync("""SELECT "ParentId" FROM "Page" WHERE "Id"=@page""", ("page", Guid.Parse(pageId)))) is Guid parent ? parent.ToString() : null;

    private static async Task<long> PublicationsAsync(McpSite f, string pageId)
        => (long)(await f.Site.ScalarAsync("""SELECT count(*) FROM "PagePublication" WHERE "PageId"=@page""", ("page", Guid.Parse(pageId))))!;

    private static async Task<string?> AuditAsync(McpSite f, string writerId)
        => (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", ',' ORDER BY "Id") FROM "ApiActivity"
            WHERE "ActorId"=@writer::uuid AND "Channel"='assistant' AND "TokenId" IS NULL
            """, ("writer", writerId));
}
