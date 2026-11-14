using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The agent turn in the conversation flow (WP-14): prepared with <c>task: assistant.agent</c>, completed as JSON or NDJSON. A tool call
/// runs as the signed-in browser user of the request, not as a token and not as the system, so content rights and G5 hold exactly as
/// over /mcp. The model is scripted; the tool results it received are captured by the script.
/// </summary>
public sealed class AssistantAgentTests
{
    [PostgreSqlFact]
    public async Task A_turn_runs_tools_as_the_user_stores_steps_without_arguments_and_cites_only_returned_pages()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        var open = await f.SpaceAsync("Provoz");
        var closed = await f.SpaceAsync("Vedení");
        var excluded = await f.SpaceAsync("Smlouvy");
        await f.GrantAsync(open, readerId, "Reader");
        await f.GrantAsync(excluded, readerId, "Reader");
        var readable = await PageAsync(f.Admin, open, "Připojení k VPN", Rich(Paragraph("Postup vpnkoala pro všechny.")), "Postup vpnkoala pro všechny.");
        await PageAsync(f.Admin, closed, "VPN vedení", Rich(Paragraph("Tajné vpnkoala.")), "Tajné vpnkoala.");
        await PageAsync(f.Admin, excluded, "VPN smlouvy", Rich(Paragraph("Vyloučené vpnkoala.")), "Vyloučené vpnkoala.");
        await f.ExcludeFromAiAsync(excluded);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, readerId);

        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var start = fake.Calls.Count; // the class shares the site and its fake
        var results = new Dictionary<string, object?>();
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (!messages.Any(message => message.Role == ChatRole.Tool))
            {
                return new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("spaces", "atlas_list_spaces", new Dictionary<string, object?>()),
                    new FunctionCallContent("search", "atlas_search", new Dictionary<string, object?> { ["query"] = "vpnkoala" }),
                    new FunctionCallContent("working", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = readable.Id, ["version"] = "working" }),
                    new FunctionCallContent("open", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = readable.Id }),
                    new FunctionCallContent("write", "atlas_set_tags", new Dictionary<string, object?> { ["pageId"] = readable.Id, ["tags"] = new[] { "x" } }),
                ]);
            }
            foreach (var result in messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()))
            {
                results[result.CallId] = result.Result;
            }
            // Names a page the tools returned and one they never did: only the first may become a source.
            return new ChatMessage(ChatRole.Assistant, "Postup **vpnkoala** je na stránce Připojení k VPN.\nO stránce VPN vedení nic nevím.");
        });

        var prepared = await PrepareAsync(reader, "Které prostory vidím a kde je vpnkoala?");
        Assert.Equal("preparing", prepared["state"]!.GetValue<string>());
        Assert.Empty(prepared["sources"]!.AsArray());
        var answer = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);

        // The reader's spaces: not the one without a grant, not the one excluded from AI.
        Assert.Contains("Provoz", Show(results["spaces"]));
        Assert.DoesNotContain("Vedení", Show(results["spaces"]));
        Assert.DoesNotContain("Smlouvy", Show(results["spaces"]));
        Assert.Contains(readable.Id, Show(results["search"]));
        Assert.DoesNotContain("VPN vedení", Show(results["search"]));
        Assert.DoesNotContain("VPN smlouvy", Show(results["search"]));
        // A reader asking for the working version gets the handler's 404, as on the web (spec §7.1), not the profile's refusal.
        Assert.Contains("Chyba 404", Show(results["working"]));
        Assert.DoesNotContain("working_version_unavailable", Show(results["working"]));
        Assert.Contains("vpnkoala", Show(results["open"]));
        // The tool ran in the request's HttpContext, which carries the session's marker: the opening is the assistant's, without a token.
        Assert.Equal("assistant:-", await f.Site.ScalarAsync("""
            SELECT string_agg("Channel" || ':' || coalesce("ApiTokenId"::text,'-'), ',') FROM "PageAccessEvent"
            WHERE "PageId"=@page AND "ActorId"=@reader::uuid AND "Action"='page.opened'
            """, ("page", Guid.Parse(readable.Id)), ("reader", readerId)));
        // A write tool outside the offer: the loop answers the model itself, the call never reaches Atlas and is no step.
        Assert.Contains("not found", Show(results["write"]));
        Assert.Empty(await TagsAsync(f, readable.Id));

        // The answer: plain sentences (Markdown marks removed); the one source is the page the tools returned, cited where named.
        Assert.Equal(("answered", "agent@7"),(answer["state"]!.GetValue<string>(), await f.Site.ScalarAsync(
            """SELECT "PromptVersion" FROM "AiMessage" WHERE "Id"=@id""", ("id", Guid.Parse(answer["id"]!.GetValue<string>()))) as string));
        Assert.Equal(["Postup vpnkoala je na stránce Připojení k VPN.", "O stránce VPN vedení nic nevím."],
            answer["sentences"]!.AsArray().Select(sentence => sentence!["text"]!.GetValue<string>()));
        Assert.Equal([[1], []], answer["sentences"]!.AsArray().Select(sentence => sentence!["sources"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray()));
        var source = Assert.Single(answer["sources"]!.AsArray())!;
        Assert.Equal((1, readable.Id, "Připojení k VPN", true), (source["number"]!.GetValue<int>(), source["pageId"]!.GetValue<string>(),
            source["title"]!.GetValue<string>(), source["cited"]!.GetValue<bool>()));
        // Steps: tool and outcome only, in call order (the starting search of the question first); never an argument value.
        Assert.Equal("""[{"tool":"atlas_search","ok":true},{"tool":"atlas_list_spaces","ok":true},{"tool":"atlas_search","ok":true},{"tool":"atlas_get_page","ok":false},{"tool":"atlas_get_page","ok":true}]""",
            answer["steps"]!.ToJsonString());
        var stored = (string)(await f.Site.ScalarAsync("""SELECT CAST("Steps" AS text) FROM "AiMessage" WHERE "Id"=@id""", ("id", Guid.Parse(answer["id"]!.GetValue<string>()))))!;
        Assert.DoesNotContain("vpnkoala", stored);
        Assert.DoesNotContain(readable.Id, stored);
        // The reopened conversation shows the same steps and source.
        var reopened = await AtlasHttpSite.Json(await reader.GetAsync($"/api/ai/conversations/{prepared["conversationId"]!.GetValue<string>()}"), HttpStatusCode.OK);
        var last = reopened["messages"]!.AsArray()[^1]!;
        Assert.Equal(answer["steps"]!.ToJsonString(), last["steps"]!.ToJsonString());
        Assert.Equal(readable.Id, last["sources"]![0]!["pageId"]!.GetValue<string>());

        // Offered tools: the read scope only; every model call ran on the agent's task.
        var offered = fake.Calls.Skip(start).First(call => call.Task == "assistant.agent").ChatOptions!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.Contains("atlas_search", offered);
        Assert.DoesNotContain("atlas_set_tags", offered);
        Assert.DoesNotContain("atlas_move_page", offered);
        Assert.DoesNotContain("atlas_download_attachment", offered);
        Assert.DoesNotContain(fake.Calls.Skip(start), call => call.Kind == "chat" && call.Task != "assistant.agent");

        // The daily limit of assistant.agent counts turns: with a limit of 1, the next one is refused before any model call.
        var calls = fake.Calls.Count;
        await PolicyAsync(f, readerId, agentDailyLimit: 1);
        var limited = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/conversations/{prepared["conversationId"]!.GetValue<string>()}/messages",
            new { question = "A dál?", scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.TooManyRequests);
        Assert.Equal("ai_limit", limited["code"]!.GetValue<string>());

        // D17: without the "AI s nástroji" right the turn is refused before any model call; the answer from the wiki still works.
        await PolicyAsync(f, readerId, tools: false);
        var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question = "Ahoj?", scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.Forbidden);
        Assert.Equal(AiGate.ToolsForbiddenCode, refused["code"]!.GetValue<string>());
        Assert.Equal(calls, fake.Calls.Count);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question = "Kde je vpnkoala?", scope = new { kind = "wiki" }, task = "chat.answer" }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question = "Ahoj?", scope = new { kind = "wiki" }, task = "agent" })).StatusCode);
    }

    /// <summary>
    /// WP-12 (D19) through the conversation: the turn offers the router's groups ∩ existing ∩ allowed tools, with their short
    /// descriptions; a write tool in an offered group is refused at the write seam even for a Writer and changes nothing; a tool outside
    /// the offer never runs; a long result is cut to the cap with a note. With <c>Accept: application/x-ndjson</c> the steps stream
    /// before the message.
    /// </summary>
    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task A_turn_offers_only_the_selected_groups_refuses_writes_caps_output_and_streams_steps()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests));
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("Provoz");
        await f.GrantAsync(space, writerId, "Writer");
        var vpn = await PageAsync(f.Admin, space, "Připojení k VPN", Rich(Paragraph("Postup.")), "Postup.");
        var chunk = new string('x', 590) + " ";
        var big = await PageAsync(f.Admin, space, "Velká příručka", Rich([.. Enumerable.Range(0, 30).Select(i => Paragraph(chunk + i))]),
            string.Join("\n\n", Enumerable.Range(0, 30).Select(i => chunk + i)));
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        var before = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{vpn.Id}"), HttpStatusCode.OK);

        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var results = new Dictionary<string, object?>();
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (messages[^1].Role == ChatRole.Tool)
            {
                foreach (var result in messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()))
                {
                    results[result.CallId] = result.Result;
                }
                return new ChatMessage(ChatRole.Assistant, "Přesun musíte potvrdit; v příručce Velká příručka je postup.");
            }
            return !messages.Last(message => message.Role == ChatRole.User).Text.Contains("VPN")
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("big", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = big.Id })])
                : new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("move", "atlas_move_page", new Dictionary<string, object?> { ["pageId"] = vpn.Id, ["expectedTreeVersion"] = 999_999 }),
                    new FunctionCallContent("publish", "atlas_publish", new Dictionary<string, object?> { ["pageId"] = vpn.Id, ["expectedVersion"] = vpn.Version }),
                    new FunctionCallContent("download", "atlas_download_attachment", new Dictionary<string, object?> { ["attachmentId"] = Guid.NewGuid().ToString() }),
                ]);
        });

        // Question 1 (JSON): zaklad + struktura (read only). The long page is cut to the cap with the note and no structured content.
        var first = fake.Calls.Count;
        var prepared = await PrepareAsync(writer, "Které prostory vidím a co je v příručce?");
        var json = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);
        Assert.Equal("answered", json["state"]!.GetValue<string>());
        var offered = fake.Calls.Skip(first).First(call => call.Task == "assistant.agent").ChatOptions!.Tools!;
        Assert.Equal(["atlas_find_pages_by_tag", "atlas_get_labels", "atlas_get_page", "atlas_get_space_tree", "atlas_list_appearance",
            "atlas_list_pages", "atlas_list_spaces", "atlas_search"], offered.Select(tool => tool.Name).Order());
        Assert.StartsWith("Hledá v publikovaných stránkách podle významu", offered.Single(tool => tool.Name == "atlas_search").Description);
        var cut = Assert.IsType<TextContent>(results["big"]).Text;
        Assert.True(cut.Length <= AssistantToolSession.OutputCap, $"{cut.Length} characters");
        Assert.EndsWith(AssistantToolSession.TruncationNote, cut);
        Assert.Contains("Velká příručka", cut);
        // The page was read whole before the cut: it is the answer's source.
        Assert.Equal(big.Id, json["sources"]![0]!["pageId"]!.GetValue<string>());
        // The tool still ran as the assistant channel of the request.
        Assert.Equal("assistant:-", await f.Site.ScalarAsync("""
            SELECT string_agg("Channel" || ':' || coalesce("ApiTokenId"::text,'-'), ',') FROM "PageAccessEvent"
            WHERE "PageId"=@page AND "ActorId"=@writer::uuid AND "Action"='page.opened'
            """, ("page", Guid.Parse(big.Id)), ("writer", writerId)));

        // Question 2 (NDJSON, a new conversation): zaklad + presuny with the tree it needs. A move is a proposal since WP-40, previewed
        // under the user's rights: a stale tree version is a tool error the model reads, and nothing is proposed or changed;
        // publish (no "publikuj" in the last question, D18) and the attachment download are never offered, so the loop refuses them
        // without reaching Atlas and without a step.
        var second = fake.Calls.Count;
        var move = await PrepareAsync(writer, "Přesuň stránku Připojení k VPN na konec");
        var lines = await StreamAsync(writer, move["messageId"]!.GetValue<string>());
        Assert.Equal(["atlas_find_pages_by_tag", "atlas_get_labels", "atlas_get_page", "atlas_get_space_tree", "atlas_list_appearance", "atlas_list_pages",
            "atlas_list_spaces", "atlas_move_page", "atlas_move_page_to_space", "atlas_search"], fake.Calls[second].ChatOptions!.Tools!.Select(tool => tool.Name).Order());
        Assert.Contains("version_conflict", Show(results["move"]));
        Assert.Contains("not found", Show(results["publish"]));
        Assert.Contains("not found", Show(results["download"]));
        Assert.Equal(["""{"type":"step","id":"s1","phase":"start","tool":"atlas_move_page"}""",
            """{"type":"step","id":"s1","phase":"end","tool":"atlas_move_page","ok":false}"""], lines[..2].Select(line => line.ToJsonString()));
        Assert.Equal(3, lines.Length);
        Assert.Equal("message", lines[2]["type"]!.GetValue<string>());
        Assert.Equal(("answered", """[{"tool":"atlas_move_page","ok":false}]"""),
            (lines[2]["message"]!["state"]!.GetValue<string>(), lines[2]["message"]!["steps"]!.ToJsonString()));
        var after = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{vpn.Id}"), HttpStatusCode.OK);
        Assert.Equal(before.ToJsonString(), after.ToJsonString());

        // A finished turn asked again fails before any line: the usual Problem Details, not a stream.
        await ProblemAsync(writer, move["messageId"]!.GetValue<string>(), "turn_finished");
    }

    /// <summary>Plan 3.5 I: a zakázka page with many requirement blocks reached the assistant cut at its output cap, footer and cursor
    /// gone. On the assistant channel the list comes in parts that fit whole, each saying how to read the next, and the cursor goes on.</summary>
    [PostgreSqlFact]
    public async Task A_long_requirement_list_reaches_the_assistant_in_whole_parts_it_can_follow()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests), settings => settings["Modules:Projector:Enabled"] = "true");
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("Nasazení");
        await f.GrantAsync(space, writerId, "Writer");
        var notes = string.Concat(Enumerable.Repeat("Dlouhá poznámka k požadavku. ", 20)).TrimEnd();
        var page = await PageAsync(f.Admin, space, "Dlouhá zakázka", Projector.ProjectorDocuments.Json(Enumerable.Range(0, 40)
            .Select(i => (JsonNode)Projector.ProjectorDocuments.Block($"00000000-0000-4000-8000-{i + 1:D12}", $"req.{i}", $"Požadavek {i}",
                content: [Projector.ProjectorDocuments.Paragraph(notes)])).ToArray()), "");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);

        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var texts = new List<string>();
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (messages[^1].Role == ChatRole.Tool)
            {
                // A part that still fits with its structured content arrives as the whole serialized result: its text blocks.
                var result = messages[^1].Contents.OfType<FunctionResultContent>().Single().Result;
                texts.Add(result is JsonElement ? ToolText(result) : Show(result));
                if (System.Text.RegularExpressions.Regex.Match(texts[^1], "cursor \"([^\"]+)\"") is { Success: true } next && texts.Count < 10)
                {
                    return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"part{texts.Count}", "projector_get_requirements",
                        new Dictionary<string, object?> { ["pageId"] = page.Id, ["cursor"] = next.Groups[1].Value })]);
                }
                return new ChatMessage(ChatRole.Assistant, "Na stránce Dlouhá zakázka je 40 požadavků.");
            }
            return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("part0", "projector_get_requirements", new Dictionary<string, object?> { ["pageId"] = page.Id })]);
        });

        var prepared = await PrepareAsync(writer, "Jaké požadavky má zakázka?");
        var json = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);

        Assert.Equal("answered", json["state"]!.GetValue<string>());
        Assert.True(texts.Count > 1, $"{texts.Count} parts");
        Assert.All(texts, text => Assert.True(text.Length <= AssistantToolSession.OutputCap && !text.EndsWith(AssistantToolSession.TruncationNote), text[^200..]));
        Assert.All(Enumerable.Range(0, 40), i => Assert.Contains($"Požadavek {i} ", string.Join('\n', texts)));
        Assert.DoesNotContain("cursor \"", texts[^1]);
    }

    /// <summary>
    /// A model that only calls tools until the loop's limit leaves no text: one last call without tools answers from what was found, the
    /// steps are kept; when that call is empty too, the turn fails as an unusable output.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_turn_that_ends_on_tool_calls_gets_one_final_call_without_tools_and_fails_only_if_that_is_empty()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        var open = await f.SpaceAsync("Provoz");
        await f.GrantAsync(open, readerId, "Reader");
        await PageAsync(f.Admin, open, "Připojení k VPN", Rich(Paragraph("Postup vpnkoala.")), "Postup vpnkoala.");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, readerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var start = fake.Calls.Count;
        var empty = false;
        fake.Script = messages => messages[^1].Text.Contains("Další vyhledávání už není možné")
            ? new ChatMessage(ChatRole.Assistant, empty ? "" : "Nic jsem nenašel; chybí obsah vyloučený z AI.")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c" + messages.Count, "atlas_search", new Dictionary<string, object?> { ["query"] = "x" })]);

        var prepared = await PrepareAsync(reader, "Kde je vpnkoala?");
        var answer = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);
        Assert.Equal("answered", answer["state"]!.GetValue<string>());
        Assert.Equal("Nic jsem nenašel; chybí obsah vyloučený z AI.", answer["sentences"]![0]!["text"]!.GetValue<string>());
        // The starting search and one call per round.
        Assert.Equal(AssistantAgentRunner.MaxToolRounds + 1, answer["steps"]!.AsArray().Count);
        var agentCalls = fake.Calls.Skip(start).Where(call => call.Task == "assistant.agent").ToArray();
        // After 8 rounds the loop itself calls without tools, yet a model may still answer with calls; then the forced call.
        Assert.Equal(AssistantAgentRunner.MaxToolRounds + 2, agentCalls.Length);
        Assert.All(agentCalls[..AssistantAgentRunner.MaxToolRounds], call => Assert.NotEmpty(call.ChatOptions!.Tools!));
        Assert.True(agentCalls[^1].ChatOptions!.Tools is null or { Count: 0 });

        empty = true;
        var second = await PrepareAsync(reader, "A kde je vpnkoala jinak?");
        using var response = await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{second["messageId"]!.GetValue<string>()}/complete");
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("failed:invalid_output", await f.Site.ScalarAsync(
            """SELECT "State" || ':' || "ErrorCode" FROM "AiMessage" WHERE "Id"=@id""", ("id", Guid.Parse(second["messageId"]!.GetValue<string>()))));
    }

    /// <summary>
    /// WP-30 (spec §7.1, S1): an editor's turn reads the working version when <c>assistant.working-text</c> may send working text to the
    /// agent's chat profile, decided at each call: a company profile always; a cloud one only with the admin's permission; never with the
    /// task switched off. A refusal is the tool error 403 working_version_unavailable with the reason; the published version still reads.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_working_version_reaches_the_model_only_where_the_profile_may_receive_working_text()
    {
        await using var f = await McpSite.CreateAsync(enabled: false, isolated: true); // isolated: saves the chat profile (AiProfile, one per installation) and the task assistant.working-text (AiFeature)
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("Provoz");
        await f.GrantAsync(space, writerId, "Writer");
        var page = await PageAsync(f.Admin, space, "Připojení k VPN", Rich(Paragraph("Publikovaný publikoala.")), "Publikovaný publikoala.");
        await SaveAsync(f.Admin, page.Id, page.Version, "Připojení k VPN", Rich(Paragraph("Rozepsaný wikikoala.")), "Rozepsaný wikikoala.");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var results = new Dictionary<string, object?>();
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (!messages.Any(message => message.Role == ChatRole.Tool))
            {
                return new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("working", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = page.Id, ["version"] = "working" }),
                    new FunctionCallContent("published", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = page.Id }),
                ]);
            }
            foreach (var result in messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()))
            {
                results[result.CallId] = result.Result;
            }
            return new ChatMessage(ChatRole.Assistant, "Postup je na stránce Připojení k VPN.");
        });
        async Task<(string Working, string Published)> TurnAsync()
        {
            results.Clear();
            var prepared = await PrepareAsync(writer, "Co je v rozepsané verzi stránky Připojení k VPN?");
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
                HttpStatusCode.OK);
            return (Show(results["working"]), Show(results["published"]));
        }
        async Task FeatureAsync(bool enabled, bool cloud)
        {
            var current = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/ai/features"), HttpStatusCode.OK);
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/features", new
            {
                items = new[] { new { task = "assistant.working-text", enabled, cloudWorkingText = (bool?)cloud, dailyLimit = (int?)null } },
                expectedVersion = current["version"]?.GetValue<string>(),
            }), HttpStatusCode.OK);
        }

        // A company profile always may.
        var (working, published) = await TurnAsync();
        Assert.Contains("wikikoala", working);
        Assert.Contains("publikoala", published);

        // The chat profile moves to the cloud: without the permission the working version is refused with the reason, the published reads.
        var chat = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/ai/profiles/chat"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/profiles/chat", new
        {
            enabled = true, provider = "openai-compatible", location = "cloud", baseUrl = "https://chat.example/v1", model = "gpt-test",
            timeoutSeconds = 120, maxConcurrency = 2, nativeJsonSchema = true, apiKey = "sk-cloud-test", clearApiKey = false,
            expectedVersion = chat["version"]?.GetValue<string>(),
        }), HttpStatusCode.OK);
        await f.Site.Services.GetRequiredService<AiProbeRunner>().RunOnceAsync(DateTimeOffset.UtcNow, default);
        (working, published) = await TurnAsync();
        Assert.Contains("working_version_unavailable", working);
        Assert.Contains("v cloudu", working);
        Assert.DoesNotContain("wikikoala", working);
        Assert.Contains("publikoala", published);

        // The admin allows working text to the cloud for this task: the next call reads it.
        await FeatureAsync(enabled: true, cloud: true);
        (working, _) = await TurnAsync();
        Assert.Contains("wikikoala", working);

        // The task switched off: refused again, with its own reason.
        await FeatureAsync(enabled: false, cloud: true);
        (working, published) = await TurnAsync();
        Assert.Contains("working_version_unavailable", working);
        Assert.Contains("je vypnut", working);
        Assert.DoesNotContain("wikikoala", working);
        Assert.Contains("publikoala", published);
    }

    /// <summary>Owner 2. 11. 2026: the projector tools follow the same gate. With <c>assistant.working-text</c> allowed an editor's turn reads
    /// a requirement's unsaved-to-publication change; switched off, the same calls read the published requirement only.</summary>
    [PostgreSqlFact]
    public async Task The_projector_tools_read_the_working_rows_only_where_the_gate_allows_working_text()
    {
        await using var f = await McpSite.CreateAsync(enabled: false, isolated: true, settings: settings => settings["Modules:Projector:Enabled"] = "true"); // isolated: switches the task assistant.working-text (AiFeature, one per installation)
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("Nasazení");
        await f.GrantAsync(space, writerId, "Writer");
        const string block = "00000000-0000-4000-8000-0000000000b1";
        var page = await PageAsync(f.Admin, space, "Zakázka KBN", Projector.ProjectorDocuments.Json(
            Projector.ProjectorDocuments.Block(block, "portal.ssl", "Publikovaný publikoala")), "");
        await SaveAsync(f.Admin, page.Id, page.Version, "Zakázka KBN", Projector.ProjectorDocuments.Json(
            Projector.ProjectorDocuments.Block(block, "portal.ssl", "Rozepsaný wikikoala", state: "waiting_for_input")), "");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var results = new Dictionary<string, object?>();
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (!messages.Any(message => message.Role == ChatRole.Tool))
            {
                return new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("page", "projector_get_requirements", new Dictionary<string, object?> { ["pageId"] = page.Id }),
                    new FunctionCallContent("space", "projector_query", new Dictionary<string, object?> { ["spaceId"] = space }),
                ]);
            }
            foreach (var result in messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()))
            {
                results[result.CallId] = result.Result;
            }
            return new ChatMessage(ChatRole.Assistant, "Zakázka KBN má jeden požadavek.");
        });
        async Task<string> TurnAsync()
        {
            results.Clear();
            var prepared = await PrepareAsync(writer, "Jaké požadavky má zakázka KBN?");
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
                HttpStatusCode.OK);
            return Show(results["page"]) + "\n" + Show(results["space"]);
        }

        var allowed = await TurnAsync();
        Assert.Contains("wikikoala", allowed);
        Assert.DoesNotContain("publikoala", allowed);

        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/ai/features"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/features", new
        {
            items = new[] { new { task = "assistant.working-text", enabled = false, cloudWorkingText = (bool?)false, dailyLimit = (int?)null } },
            expectedVersion = current["version"]?.GetValue<string>(),
        }), HttpStatusCode.OK);
        var refused = await TurnAsync();
        Assert.DoesNotContain("wikikoala", refused);
        Assert.Contains("publikoala", refused);
    }

    /// <summary>WP-14: a published page the tools read is a source; the user losing access to it while the model writes withholds the
    /// whole answer, its steps and its proposal (a working-version read is no source and never withholds, spec §7.1).</summary>
    [PostgreSqlFact]
    public async Task A_published_source_revoked_between_the_tool_read_and_the_release_withholds_the_answer()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests));
        var (writer, writerId) = await f.UserAsync("writer", level: null);
        var space = await f.SpaceAsync("Provoz");
        await f.GrantAsync(space, writerId, "Writer");
        var page = await PageAsync(f.Admin, space, "Připojení k VPN", Rich(Paragraph("Postup.")), "Postup.");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, writerId);
        f.Site.Services.GetRequiredService<FakeAiClientFactory>().Script = messages =>
        {
            if (messages[^1].Role != ChatRole.Tool)
            {
                return new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("working", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = page.Id, ["version"] = "working" }),
                    new FunctionCallContent("published", "atlas_get_page", new Dictionary<string, object?> { ["pageId"] = page.Id }),
                ]);
            }
            f.Site.ExecuteAsync("""
                UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "SpaceId"=@space::uuid AND "UserId"=@user::uuid AND "ActiveTo" IS NULL
                """, ("space", space), ("user", writerId)).GetAwaiter().GetResult();
            return new ChatMessage(ChatRole.Assistant, "Postup je na stránce Připojení k VPN.");
        };

        var prepared = await PrepareAsync(writer, "Jak se připojit k VPN?");
        var lines = await StreamAsync(writer, prepared["messageId"]!.GetValue<string>());
        Assert.Equal("withheld", lines[^1]["message"]!["state"]!.GetValue<string>());
        Assert.Equal((0L, 0L), ((long)(await f.Site.ScalarAsync("""SELECT count(*) FROM "AiMessage" WHERE "Id"=@id::uuid AND "Steps" IS NOT NULL""",
                ("id", prepared["messageId"]!.GetValue<string>())))!,
            (long)(await f.Site.ScalarAsync("""SELECT count(*) FROM "AiSourceDependency" WHERE "MessageId"=@id::uuid""", ("id", prepared["messageId"]!.GetValue<string>())))!));
    }

    /// <summary>
    /// agent@5: a question's turn starts with atlas_search of the question run by the server (a step, the user's rights, the sources), its
    /// result already in the history of the first model call, so a question the passages answer takes one model call; a question in a
    /// space searches that space. A task (a write group) starts without it.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_question_starts_with_the_server_s_search_and_one_model_call_answers_from_its_passages()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantAgentTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        var open = await f.SpaceAsync("Provoz");
        var other = await f.SpaceAsync("Kancelář");
        await f.GrantAsync(open, readerId, "Reader");
        await f.GrantAsync(other, readerId, "Reader");
        var body = "Klient vpnkoala nainstalujete z portálu. " + string.Join(' ', Enumerable.Repeat("Přihlásíte se firemním účtem.", 20)) + " KONEC-PASÁŽE.";
        var vpn = await PageAsync(f.Admin, open, "Připojení k VPN", IndexFixture.Rich(body, ("Postup", "postup")), "## Postup\n" + body);
        await PageAsync(f.Admin, other, "Tiskárny vpnkoala", Rich(Paragraph("Klient vpnkoala pro tiskárny v kanceláři.")), "Klient vpnkoala pro tiskárny v kanceláři.");
        // The search query (the question without its question words) and the VPN section mean the same.
        f.Site.Services.GetRequiredService<FakeAiClientFactory>().Meaning = text => text.Contains("Klient vpnkoala nainstalujete", StringComparison.Ordinal)
            || text.StartsWith("klient vpnkoala", StringComparison.Ordinal) ? "remote access client" : text;
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, readerId);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var seen = new List<IReadOnlyList<ChatMessage>>();
        fake.Script = messages =>
        {
            seen.Add(messages.ToArray());
            return new ChatMessage(ChatRole.Assistant, "Klient vpnkoala se instaluje z portálu, píše stránka Připojení k VPN.");
        };

        var calls = fake.Calls.Count(call => call.Task == "assistant.agent");
        var prepared = await PrepareAsync(reader, "Kde je klient vpnkoala?");
        var answer = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);

        Assert.Equal(1, fake.Calls.Count(call => call.Task == "assistant.agent") - calls);
        var history = Assert.Single(seen);
        var search = history.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal((AssistantAgentRunner.StartingCallId, "atlas_search", "Kde je klient vpnkoala?"), (search.CallId, search.Name, search.Arguments!["query"]?.ToString()));
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], history.Skip(1).Select(message => message.Role));
        var result = ToolText(history[^1].Contents.OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("Připojení k VPN", result);
        Assert.Contains("Tiskárny vpnkoala", result);
        // The passage text of the section, whole, not the 320-character passage of the web.
        Assert.Contains("KONEC-PASÁŽE", result);
        Assert.Equal("""[{"tool":"atlas_search","ok":true}]""", answer["steps"]!.ToJsonString());
        Assert.Equal(vpn.Id, Assert.Single(answer["sources"]!.AsArray())!["pageId"]!.GetValue<string>());

        // In a space: that space only.
        seen.Clear();
        var inSpace = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question = "Kde je klient vpnkoala?", scope = new { kind = "space", spaceId = other }, task = "assistant.agent" }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{inSpace["messageId"]!.GetValue<string>()}/complete"), HttpStatusCode.OK);
        var spaced = ToolText(Assert.Single(seen)[^1].Contents.OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("Tiskárny vpnkoala", spaced);
        Assert.DoesNotContain("Připojení k VPN", spaced);

        // A task: no starting search.
        seen.Clear();
        var task = await PrepareAsync(reader, "Přidej tag vpn stránce Připojení k VPN");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{task["messageId"]!.GetValue<string>()}/complete"), HttpStatusCode.OK);
        Assert.DoesNotContain(Assert.Single(seen), message => message.Role == ChatRole.Tool);
    }

    private static async Task<JsonNode> PrepareAsync(HttpClient client, string question)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question, scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.OK);

    private static async Task<HttpResponseMessage> CompleteNdjsonAsync(HttpClient client, string messageId)
    {
        var csrf = await AtlasHttpSite.Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/ai/messages/{messageId}/complete");
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        return await client.SendAsync(request);
    }

    private static async Task<JsonNode[]> StreamAsync(HttpClient client, string messageId)
    {
        using var response = await CompleteNdjsonAsync(client, messageId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("\n", body);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
    }

    private static async Task ProblemAsync(HttpClient client, string messageId, string code)
    {
        using var response = await CompleteNdjsonAsync(client, messageId);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await AtlasHttpSite.Json(response, HttpStatusCode.Conflict))["code"]!.GetValue<string>());
    }

    /// <summary>The text blocks of a tool result the model read (the serialized <c>CallToolResult</c>).</summary>
    private static string ToolText(object? result)
        => string.Join('\n', ((JsonElement)result!).GetProperty("content").EnumerateArray().Select(block => block.GetProperty("text").GetString()));

    private static string Show(object? result) => result switch
    {
        TextContent text => text.Text,
        JsonElement json => json.GetRawText(),
        _ => result?.ToString() ?? "",
    };

    private static async Task<string[]> TagsAsync(McpSite f, string pageId)
        => (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{pageId}/tags"), HttpStatusCode.OK))["tags"]!.AsArray()
            .Select(tag => tag!["name"]!.GetValue<string>()).ToArray();

    private static async Task PolicyAsync(McpSite f, string userId, bool tools = true, int? agentDailyLimit = null)
    {
        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/admin/users/{userId}/ai-policy"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/admin/users/{userId}/ai-policy",
            new { enabled = true, toolsEnabled = tools, searchAnswerDailyLimit = (int?)null, chatAnswerDailyLimit = (int?)null, pageSummaryDailyLimit = (int?)null,
                agentDailyLimit, expectedVersion = current["version"]?.GetValue<string>() }), HttpStatusCode.OK);
    }
}
