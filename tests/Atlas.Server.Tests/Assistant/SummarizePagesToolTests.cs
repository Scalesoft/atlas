using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>WP-50: <c>atlas_summarize_pages</c> in an agent turn: published text only, G5 and rights through the shared page reader, one model call
/// per readable page plus one merge, usage per call, the summarized pages as the answer's sources, at most 20 pages.</summary>
public sealed class SummarizePagesToolTests
{
    private const string PageSystem = "Shrň poskytnuté části";
    private const string MergeSystem = "Slouč shrnutí";

    [PostgreSqlFact]
    public async Task Summaries_cover_readable_published_pages_only_say_one_was_unavailable_record_usage_and_cite_the_pages()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(SummarizePagesToolTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        var open = await f.SpaceAsync("Provoz");
        var excluded = await f.SpaceAsync("Smlouvy");
        await f.GrantAsync(open, readerId, "Reader");
        await f.GrantAsync(excluded, readerId, "Reader");
        var passwords = await PageAsync(f.Admin, open, "Pravidla hesel", Rich(Paragraph("Heslo má alespoň PUBLIKOVANO 14 znaků.")), "Heslo má alespoň PUBLIKOVANO 14 znaků.");
        // A newer working version that must never reach the model.
        await SaveAsync(f.Admin, passwords.Id, passwords.Version, "Pravidla hesel", Rich(Paragraph("TAJNY-KONCEPT")), "TAJNY-KONCEPT");
        var backups = await PageAsync(f.Admin, open, "Zálohování", Rich(Paragraph("Zálohy se dělají denně.")), "Zálohy se dělají denně.");
        var contract = await PageAsync(f.Admin, excluded, "Smlouva o službách", Rich(Paragraph("Vyloučená smlouva.")), "Vyloučená smlouva.");
        await f.ExcludeFromAiAsync(excluded);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, readerId);

        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var start = fake.Calls.Count; // the class shares the site and its fake
        var since = DateTimeOffset.UtcNow;
        object? result = null;
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            var system = messages[0].Text;
            if (system.StartsWith(PageSystem))
            {
                return new ChatMessage(ChatRole.Assistant, """{"answerable":true,"sentences":[{"text":"Bod shrnutí stránky.","sources":[1]}]}""");
            }
            if (system.StartsWith(MergeSystem))
            {
                return new ChatMessage(ChatRole.Assistant, "Společné shrnutí obou stránek.");
            }
            if (messages[^1].Role != ChatRole.Tool)
            {
                return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("sum", "atlas_summarize_pages",
                    new Dictionary<string, object?> { ["pageIds"] = new[] { passwords.Id, backups.Id, contract.Id }, ["focus"] = "bezpečnost" })]);
            }
            result = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Single().Result;
            return new ChatMessage(ChatRole.Assistant, "Společné shrnutí obou stránek je hotové.");
        });

        var prepared = await PrepareAsync(reader, "Shrň mi stránky o hesle, zálohách a smlouvě.");
        var answer = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);

        Assert.Equal("answered", answer["state"]!.GetValue<string>());
        var text = Show(result);
        Assert.Contains("1 z 3 stránek nebylo dostupných", text);
        Assert.Contains("Společné shrnutí obou stránek.", text);
        Assert.Contains(passwords.Id, text);
        Assert.Contains(backups.Id, text);
        // The skipped page is neither named nor identified.
        Assert.DoesNotContain("Smlouva o službách", text);
        Assert.DoesNotContain(contract.Id, text);

        var calls = fake.Calls.Skip(start).Where(call => call.Kind == "chat").ToArray();
        var perPage = calls.Where(call => call.Inputs[0].StartsWith(PageSystem)).ToArray();
        Assert.Equal(2, perPage.Length);
        Assert.Single(calls, call => call.Inputs[0].StartsWith(MergeSystem));
        Assert.All(perPage, call => Assert.Contains("bezpečnost", call.Inputs[^1]));
        // Published text only: never the working draft, never the excluded page.
        Assert.Contains(perPage, call => call.Inputs.Any(input => input.Contains("PUBLIKOVANO")));
        Assert.All(calls, call => Assert.DoesNotContain(call.Inputs, input => input.Contains("TAJNY-KONCEPT") || input.Contains("Vyloučená smlouva")));

        // The summarized pages are the answer's sources (read through the shared handler), the unavailable one is not.
        var sources = answer["sources"]!.AsArray().Select(source => source!["pageId"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(new HashSet<string> { passwords.Id, backups.Id }, sources);
        Assert.Equal("""[{"tool":"atlas_search","ok":true},{"tool":"atlas_summarize_pages","ok":true}]""", answer["steps"]!.ToJsonString());

        // AiUsage: a row for every model call of the turn (2 loop calls, 2 summaries, 1 merge).
        var expected = fake.Calls.Skip(start).Count(call => call.Task == "assistant.agent");
        Assert.Equal(5, expected);
        var flusher = f.Site.Services.GetRequiredService<AiUsageFlusher>();
        var rows = 0L;
        for (var attempt = 0; attempt < 50 && rows < expected; attempt++)
        {
            await flusher.FlushAsync(default);
            rows = Convert.ToInt64(await f.Site.ScalarAsync("""SELECT count(*) FROM "AiUsage" WHERE "Task"='assistant.agent' AND "OccurredAt" >= @since""", ("since", since)));
            if (rows < expected)
            {
                await Task.Delay(100);
            }
        }
        Assert.Equal(expected, rows);
    }

    [PostgreSqlFact]
    public async Task More_than_twenty_pages_is_a_tool_error_without_any_summary_call()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(SummarizePagesToolTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        await PageAsync(f.Admin, await f.SpaceAsync("Provoz"), "Úvod", Rich(Paragraph("Text.")), "Text.");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        await PolicyAsync(f, readerId);

        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var start = fake.Calls.Count; // the class shares the site and its fake
        object? result = null;
        fake.Script = AgentScript.WithoutStartingSearch(messages =>
        {
            if (messages[^1].Role != ChatRole.Tool)
            {
                return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("sum", "atlas_summarize_pages",
                    new Dictionary<string, object?> { ["pageIds"] = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid().ToString()).ToArray() })]);
            }
            result = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Single().Result;
            return new ChatMessage(ChatRole.Assistant, "Tolik stránek najednou shrnout nejde.");
        });

        var prepared = await PrepareAsync(reader, "Shrň mi všechny stránky.");
        var answer = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/ai/messages/{prepared["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.OK);

        Assert.Contains("invalid_argument", Show(result));
        Assert.Equal("""[{"tool":"atlas_search","ok":true},{"tool":"atlas_summarize_pages","ok":false}]""", answer["steps"]!.ToJsonString());
        Assert.DoesNotContain(fake.Calls.Skip(start), call => call.Inputs.Count > 0 && call.Inputs[0].StartsWith(PageSystem));
    }

    private static async Task<JsonNode> PrepareAsync(HttpClient client, string question)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question, scope = new { kind = "wiki" }, task = "assistant.agent" }), HttpStatusCode.OK);

    private static string Show(object? result) => result switch
    {
        TextContent text => text.Text,
        JsonElement json => json.GetRawText(),
        _ => result?.ToString() ?? "",
    };

    private static async Task PolicyAsync(McpSite f, string userId)
    {
        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/admin/users/{userId}/ai-policy"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/admin/users/{userId}/ai-policy",
            new { enabled = true, toolsEnabled = true, searchAnswerDailyLimit = (int?)null, chatAnswerDailyLimit = (int?)null, pageSummaryDailyLimit = (int?)null,
                agentDailyLimit = (int?)null, expectedVersion = current["version"]?.GetValue<string>() }), HttpStatusCode.OK);
    }
}
