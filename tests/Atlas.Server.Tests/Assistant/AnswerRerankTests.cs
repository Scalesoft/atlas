using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>A scripted reranker: available or not, and a score function per call (a never-ending task is a slow provider).</summary>
internal sealed class FakeReranker : IAnswerReranker
{
    private int m_calls;

    public bool Available { get; set; } = true;
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<double>>> Score { get; set; } = documents => Prefer(documents, "");
    public int Calls => m_calls;

    public ValueTask<bool> AvailableAsync(CancellationToken ct) => ValueTask.FromResult(Available);

    public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        Interlocked.Increment(ref m_calls);
        return Score(documents);
    }

    /// <summary>1 for a document containing <paramref name="word"/>, 0 otherwise.</summary>
    public static Task<IReadOnlyList<double>> Prefer(IReadOnlyList<string> documents, string word)
        => Task.FromResult<IReadOnlyList<double>>(documents.Select(text => word.Length > 0 && text.Contains(word, StringComparison.Ordinal) ? 1.0 : 0.0).ToArray());
}

/// <summary>
/// The optional rerank step of answer sources: the pool read before the preparation, the reranker's choice stored as the sources. Without a
/// rerank profile, with a switched-off one, a slow one, one that answers garbage or one that is down, the answer gets today's sources and
/// no error; the circuit breaker stops calling a failing one.
/// </summary>
public sealed class AnswerRerankTests
{
    [Fact]
    public async Task Order_is_best_first_with_ties_in_today_s_order_and_null_for_an_unusable_answer()
    {
        var fake = new FakeReranker { Score = _ => Task.FromResult<IReadOnlyList<double>>([0.1, 0.9, 0.1, 0.5]) };
        var rerank = new AnswerRerank(fake, TimeProvider.System, NullLogger<AnswerRerank>.Instance);

        Assert.Equal([1, 3, 0, 2], await rerank.OrderAsync("q", ["a", "b", "c", "d"], default));

        // A wrong number of scores or a non-finite one is no order; a single document needs none; an unavailable reranker is not called.
        fake.Score = _ => Task.FromResult<IReadOnlyList<double>>([1.0]);
        Assert.Null(await rerank.OrderAsync("q", ["a", "b"], default));
        fake.Score = _ => Task.FromResult<IReadOnlyList<double>>([1.0, double.NaN]);
        Assert.Null(await rerank.OrderAsync("q", ["a", "b"], default));
        fake.Score = _ => Task.FromException<IReadOnlyList<double>>(new HttpRequestException("down"));
        Assert.Null(await rerank.OrderAsync("q", ["a", "b"], default));
        var calls = fake.Calls;
        Assert.Null(await rerank.OrderAsync("q", ["a"], default));
        fake.Available = false;
        Assert.Null(await rerank.OrderAsync("q", ["a", "b"], default));
        Assert.Equal(calls, fake.Calls);
    }

    [PostgreSqlFact]
    public async Task Answer_sources_follow_the_reranker_and_stay_today_s_without_it()
    {
        var reranker = new FakeReranker { Available = false };
        await using var f = await McpSite.CreateAsync(enabled: false, services: services => services.AddSingleton<IAnswerReranker>(reranker));
        await SeedAsync(f);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);

        var today = Titles(await SourcesAsync(f));
        Assert.Equal(6, today.Length);
        Assert.Equal(0, reranker.Calls);

        // The reranker prefers the last pages of today's pool: they become the first sources.
        reranker.Available = true;
        reranker.Score = documents => Task.FromResult<IReadOnlyList<double>>(documents.Select(text => text.Contains("Théta") ? 2.0 : text.Contains("Éta") ? 1.0 : 0.0).ToArray());
        var reranked = Titles(await SourcesAsync(f));
        Assert.Equal(["Théta", "Éta"], reranked.Take(2));
        Assert.Equal(6, reranked.Length);
        Assert.Equal(1, reranker.Calls);

        // A failing reranker: today's sources.
        reranker.Score = _ => Task.FromException<IReadOnlyList<double>>(new HttpRequestException("down"));
        Assert.Equal(today, Titles(await SourcesAsync(f)));
    }

    /// <summary>The real path: the rerank profile in Administrace → AI, saved, tested and used through the AI pipeline.</summary>
    [PostgreSqlFact]
    public async Task The_rerank_profile_changes_nothing_when_off_and_its_failures_never_reach_the_answer()
    {
        await using var f = await McpSite.CreateAsync(enabled: false, isolated: true); // isolated: saves the rerank profile (AiProfile, one per installation)
        await SeedAsync(f);
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        var fake = f.Site.Services.GetRequiredService<FakeAiClientFactory>();
        var today = await SourcesAsync(f);

        // A saved but switched-off profile changes nothing, byte for byte, and is never called.
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(f, "anthropic", true, null)).StatusCode);
        var off = await AtlasHttpSite.Json(await SaveAsync(f, "openai-compatible", false, null), HttpStatusCode.OK);
        Assert.Equal(today.ToJsonString(), (await SourcesAsync(f)).ToJsonString());
        Assert.Equal(0, RerankCalls(fake));

        // Switched on, it passes its test and the answer's sources follow it.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(f, "openai-compatible", true, off["version"]!.GetValue<string>())).StatusCode);
        var tested = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/admin/ai/profiles/rerank/test"), HttpStatusCode.OK);
        Assert.True(tested["ok"]!.GetValue<bool>(), tested.ToJsonString());
        await WaitAvailableAsync(f);
        fake.RerankScores = (_, documents) => documents.Select(text => text.Contains("Théta", StringComparison.Ordinal) ? 1.0 : 0.0).ToArray();
        Assert.Equal("Théta", Titles(await SourcesAsync(f))[0]);
        var call = fake.Calls.Last(entry => entry.Task == AiTaskCatalog.SearchRerank);
        Assert.Equal(("rerank", "Jak funguje vpnkoala?"), (call.Operation, call.Inputs[0]));

        // Slow: today's sources once the budget is over, without waiting for the scores. Garbage: today's sources. Down: today's sources,
        // and two failures take it away, so later answers never wait for it.
        fake.SetMode(AiProfileKind.Rerank, FakeAiMode.Slow(TimeSpan.FromSeconds(30)));
        var started = DateTime.UtcNow;
        Assert.Equal(Titles(today), Titles(await SourcesAsync(f)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "The answer waited for a slow reranker.");
        fake.SetMode(AiProfileKind.Rerank, FakeAiMode.InvalidJson);
        Assert.Equal(Titles(today), Titles(await SourcesAsync(f)));
        fake.SetMode(AiProfileKind.Rerank, FakeAiMode.Error(AiOutcome.Http5xx));
        Assert.Equal(Titles(today), Titles(await SourcesAsync(f)));
        Assert.Equal(Titles(today), Titles(await SourcesAsync(f)));
        var calls = RerankCalls(fake);
        Assert.Equal(Titles(today), Titles(await SourcesAsync(f)));
        Assert.Equal(calls, RerankCalls(fake));
    }

    private static async Task SeedAsync(McpSite f)
    {
        var space = await f.SpaceAsync("Provoz");
        foreach (var name in new[] { "Alfa", "Beta", "Gama", "Delta", "Epsilon", "Zeta", "Éta", "Théta" })
        {
            await PageAsync(f.Admin, space, name, Rich(Paragraph($"Návod vpnkoala {name}.")), $"Návod vpnkoala {name}.");
        }
    }

    private static int RerankCalls(FakeAiClientFactory fake) => fake.Calls.Count(call => call.Task == AiTaskCatalog.SearchRerank);

    private static Task<HttpResponseMessage> SaveAsync(McpSite f, string provider, bool enabled, string? expectedVersion)
        => AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/profiles/rerank", new
        {
            enabled, provider, location = "company", baseUrl = "http://reranker.example:8012/v1", model = "bge-reranker-v2-m3",
            timeoutSeconds = 10, maxConcurrency = 4, nativeJsonSchema = true, clearApiKey = false, expectedVersion,
        });

    /// <summary>The probe service verifies a new profile at once; the capability follows from memory.</summary>
    private static async Task WaitAvailableAsync(McpSite f)
    {
        var capabilities = f.Site.Services.GetRequiredService<Features.Ai.Capabilities.AiCapabilities>();
        for (var attempt = 0; !await capabilities.IsAvailableAsync(AiTaskCatalog.SearchRerank, default); attempt++)
        {
            Assert.True(attempt < 200, "The rerank profile did not become available.");
            await Task.Delay(50);
        }
    }

    /// <summary>The sources of a prepared answer (no model call), as the panel receives them.</summary>
    private static async Task<JsonArray> SourcesAsync(McpSite f)
    {
        var prepared = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/ai/conversations",
            new { origin = "assistant", question = "Jak funguje vpnkoala?", scope = new { kind = "wiki" }, task = "chat.answer" }), HttpStatusCode.OK);
        return prepared["sources"]!.AsArray();
    }

    private static string[] Titles(JsonArray sources) => sources.Select(source => source!["title"]!.GetValue<string>()).ToArray();
}
