using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.PageLinks;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>What HTTP adds to the answer flow (plan phase 3 §7, §20, R7): routes and binding, the JSON the web client reads, a
/// signed-in browser session only, Problem Details with <c>code</c> for 503, 429 and 404, and a log that never holds a question,
/// a source or an answer, at any level. The rules are in the handler tests.</summary>
public sealed class AnswerHttpTests
{
    private const string QuestionMarker = "zlatokvet";
    private const string SourceMarker = "SKRYTAZPRAVAZDROJE";
    private const string AnswerMarker = "ODPOVEDMODELUTEXT";

    [PostgreSqlFact]
    public async Task Answer_endpoints_need_a_browser_session_and_answer_problem_details()
    {
        // A site of its own (settings without shared: true): starts without AI, then saves the installation's profiles, AI switch and
        // the search.answer daily limit (AiFeature), and reads every log line of the site.
        await using var site = await AtlasHttpSite.CreateAsync("atlas_answer_http_", settings =>
        {
            settings["Logging:LogLevel:Default"] = "Trace";
            settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        });
        var question = new { origin = "search", question = $"Jak obnovit server {QuestionMarker}?", scope = new { kind = "wiki" } };

        // Only a signed-in browser session: no anonymous visitor (a sharing session has none), no personal token (R7).
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await AtlasHttpSite.SendAsync(anonymous, HttpMethod.Post, "/api/ai/conversations", question)).StatusCode);
        using var bearer = site.Client();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", "atl_not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, (await AtlasHttpSite.SendAsync(bearer, HttpMethod.Post, "/api/ai/conversations", question)).StatusCode);

        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();

        // Without AI: the canonical 503 before anything is written; a malformed request is 400 even so.
        await ProblemAsync(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/ai/conversations", question),
            HttpStatusCode.ServiceUnavailable, "ai_unavailable", "Tahle funkce teď není k dispozici.");
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "elsewhere", question = "Otázka?", scope = new { kind = "wiki" } })).StatusCode);

        var fake = site.Services.GetRequiredService<FakeAiClientFactory>();
        fake.Meaning = text => text.Contains("obnov", StringComparison.OrdinalIgnoreCase) ? "backup restore database" : text;
        await PublishAsync(admin, spaceId, "Obnova serveru", $"Postup obnovy serveru: {SourceMarker} spusťte nástroj.");
        await EnableAnswersAsync(site, admin);
        fake.ChatAnswer = FakeAi.Answers.Answerable(($"{AnswerMarker} spusťte nástroj.", [1]));

        // Step 1 and step 2 in the contract's shape.
        var prepared = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/ai/conversations", question), HttpStatusCode.OK)).AsObject();
        Assert.Equal(["conversationId", "messageId", "state", "sources", "truncated"], prepared.Select(x => x.Key));
        Assert.Equal("preparing", prepared["state"]!.GetValue<string>());
        var source = prepared["sources"]!.AsArray()[0]!.AsObject();
        Assert.Equal(["number", "type", "pageId", "spaceId", "spaceName", "title", "headingTitle", "headingId", "excerpt", "cited", "changed"],
            source.Select(x => x.Key));
        Assert.Equal(("page", "Obnova serveru", 1), (source["type"]!.GetValue<string>(), source["title"]!.GetValue<string>(), source["number"]!.GetValue<int>()));
        var messageId = prepared["messageId"]!.GetValue<string>();
        var answer = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/messages/{messageId}/complete"),
            HttpStatusCode.OK)).AsObject();
        Assert.Equal(["id", "conversationId", "role", "state", "question", "scope", "sentences", "sources", "provenance", "createdAt", "rating", "errorCode", "truncated", "steps", "proposalId"],
            answer.Select(x => x.Key));
        Assert.Equal(("answered", "assistant", messageId), (answer["state"]!.GetValue<string>(), answer["role"]!.GetValue<string>(), answer["id"]!.GetValue<string>()));
        var sentence = answer["sentences"]!.AsArray()[0]!;
        Assert.Equal(($"{AnswerMarker} spusťte nástroj.", 1), (sentence["text"]!.GetValue<string>(), sentence["sources"]!.AsArray()[0]!.GetValue<int>()));
        Assert.True(answer["sources"]!.AsArray()[0]!["cited"]!.GetValue<bool>());
        Assert.Equal("""{"location":"company","provider":"LM Studio","model":"fake-chat"}""", answer["provenance"]!.ToJsonString());
        await ProblemAsync(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/messages/{messageId}/complete"),
            HttpStatusCode.Conflict, "turn_finished", null);

        // Not the actor's (or not existing): 404, never 403.
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/messages/{Guid.NewGuid()}/complete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/conversations/{Guid.NewGuid()}/messages",
            new { question = "A dál?", scope = new { kind = "wiki" } })).StatusCode);

        // A provider outage during the answer: the same 503 the client knows, never the upstream text.
        var assistant = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/ai/conversations",
            question with { origin = "assistant" }), HttpStatusCode.OK);
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.Error(AiOutcome.Connection, "upstream PROVIDER-DETAIL"));
        await ProblemAsync(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/messages/{assistant["messageId"]!.GetValue<string>()}/complete"),
            HttpStatusCode.ServiceUnavailable, "ai_unavailable", "Tahle funkce teď není k dispozici.");
        fake.SetMode(AiProfileKind.Chat, FakeAiMode.Healthy);

        // The daily limit (the admin's own answer of today counts): 429 ai_limit.
        var features = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai/features"), HttpStatusCode.OK);
        var limit = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/features", new
        {
            items = new[] { new { task = "search.answer", enabled = true, cloudWorkingText = (bool?)null, dailyLimit = (int?)1 } },
            expectedVersion = features["version"]?.GetValue<string>(),
        }), HttpStatusCode.OK);
        Assert.Equal((true, 1), limit["items"]!.AsArray().Where(x => x!["task"]!.GetValue<string>() == "search.answer")
            .Select(x => (x!["limitable"]!.GetValue<bool>(), x["dailyLimit"]!.GetValue<int>())).Single());
        await ProblemAsync(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/ai/conversations", question),
            HttpStatusCode.TooManyRequests, "ai_limit", "Limit využití asistenta byl pro dnešek dosažen.");

        // The log was written (positive control) and holds no question, source or answer, nor the provider's text.
        Assert.Contains(site.Logs.Messages, message => message.Contains("/api/ai/conversations", StringComparison.Ordinal));
        foreach (var secret in new[] { QuestionMarker, SourceMarker, AnswerMarker, "PROVIDER-DETAIL" })
        {
            Assert.DoesNotContain(site.Logs.Messages, message => message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? title)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await AtlasHttpSite.Json(response, status);
        Assert.Equal(code, problem["code"]!.GetValue<string>());
        if (title is not null)
        {
            Assert.Equal(title, problem["title"]!.GetValue<string>());
        }
    }

    private static async Task PublishAsync(HttpClient client, string spaceId, string title, string text)
    {
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title, contentJson = PageLinksFixture.Doc(PageLinksFixture.Text(text)), markdown = text }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
    }

    // The profiles, the AI switch and the index generation are rows of the whole installation: made once per site.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AtlasHttpSite, Lazy<Task>> s_configured = new();

    /// <summary>Embeddings and chat profiles, the AI switch and an activated index generation through the admin endpoints, then
    /// the background probes verify the profiles and the capability appears. On a site shared by the tests of a class the first call
    /// does that; a later one waits until the live generation holds every eligible page (the test's own, published since).</summary>
    internal static async Task EnableAnswersAsync(AtlasHttpSite site, HttpClient admin)
    {
        await AtlasHttpSite.GrantOwnAiAsync(admin);
        var first = false;
        await s_configured.GetValue(site, _ =>
        {
            first = true;
            return new Lazy<Task>(() => ConfigureAsync(admin));
        }).Value;
        var runner = site.Services.GetRequiredService<IndexRunner>();
        if (first)
        {
            await BuildAsync(admin, runner);
        }
        var indexDeadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            // A page published just before reaches the queue through the outbox; the round after that indexes it.
            await runner.RunUntilIdleAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
            var status = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai/index"), HttpStatusCode.OK);
            var active = status["active"]!;
            if (active["indexedPages"]!.GetValue<int>() == active["eligiblePages"]!.GetValue<int>() && status["queue"]!["pending"]!.GetValue<int>() == 0)
            {
                break;
            }
            Assert.True(DateTime.UtcNow < indexDeadline, "The pages were not indexed.");
            await Task.Yield();
        }
        var capabilityDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!(await AtlasHttpSite.Json(await admin.GetAsync("/api/ai/capabilities"), HttpStatusCode.OK))["answer"]!.GetValue<bool>())
        {
            Assert.True(DateTime.UtcNow < capabilityDeadline, "Answers did not become available.");
            await Task.Yield();
        }
    }

    /// <summary>The MCP site of a test class (shared under its name): xUnit runs the tests of a class one after another, so the site's
    /// one FakeAiClientFactory and its AI settings serve one test at a time. The fake's script and meaning start fresh; its calls
    /// stay, so a test counts only the calls after its start.</summary>
    internal static async Task<Mcp.McpSite> AssistantSiteAsync(string sharedAs, Action<IDictionary<string, string?>>? settings = null)
    {
        var f = await Mcp.McpSite.CreateAsync(enabled: false, settings: settings, sharedAs: sharedAs);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        fake.Script = null;
        fake.Meaning = text => text;
        return f;
    }

    private static async Task BuildAsync(HttpClient admin, IndexRunner runner)
    {
        var generation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/ai/index/generations", new { mode = "build" }),
            HttpStatusCode.OK);
        var generationId = generation["id"]!.GetValue<string>();
        var indexDeadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            // The probe service verifies the new profile at once; a worker round does nothing before that.
            await runner.RunUntilIdleAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
            var building = (await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai/index"), HttpStatusCode.OK))["building"]!;
            if (building["indexedPages"]!.GetValue<int>() >= 1 && building["pendingTasks"]!.GetValue<int>() == 0)
            {
                break;
            }
            Assert.True(DateTime.UtcNow < indexDeadline, "The index was not built.");
            await Task.Yield();
        }
        Assert.True((await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/verify"),
            HttpStatusCode.OK))["canActivate"]!.GetValue<bool>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/activate",
            new { expectedVersion = generation["version"]!.GetValue<string>() }), HttpStatusCode.OK);
    }

    private static async Task ConfigureAsync(HttpClient admin)
    {
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/profiles/embeddings", new
        {
            enabled = true, provider = "openai-compatible", location = "company", baseUrl = "http://lmstudio.example:1234/v1", model = "text-embedding-bge-m3",
            dimensions = 1024, timeoutSeconds = 30, maxConcurrency = 4, nativeJsonSchema = true, clearApiKey = false, expectedVersion = (string?)null,
        }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/profiles/chat", new
        {
            enabled = true, provider = "openai-compatible", location = "company", baseUrl = "http://lmstudio.example:1234/v1", model = "gemma-4-26b-a4b",
            timeoutSeconds = 120, maxConcurrency = 2, nativeJsonSchema = true, clearApiKey = false, expectedVersion = (string?)null,
        }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/switch", new { enabled = true, expectedVersion = (string?)null }),
            HttpStatusCode.OK);
    }
}
