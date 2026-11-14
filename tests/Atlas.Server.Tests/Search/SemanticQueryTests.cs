using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.AiIndex;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>The query vector before the handler (plan §1.1, §11): the 300 ms budget on the fake clock, the cache, the circuit
/// breaker and every case that must not call the model at all.</summary>
public sealed class SemanticQueryTests
{
    private const string Remote = "vzdálený přístup";

    private static string RemoteMeaning(string text)
        => text.Contains(Remote, StringComparison.OrdinalIgnoreCase) || text.Contains("Připojení zvenku", StringComparison.Ordinal)
            ? "remote access from home"
            : text;

    private static int QueryCalls(HybridSearchFixture f) => f.Fake.Calls.Count(x => x.Task == AiTaskCatalog.SearchEmbedQuery);

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Query_embedding_waits_at_most_300_ms_and_a_slow_or_failing_provider_gives_lexical_results()
    {
        BudgetTimerClock? timerClock = null;
        await using var f = await HybridSearchFixture.CreateAsync(RemoteMeaning, services =>
        {
            var fakeClock = services.Last(service => service.ServiceType == typeof(TimeProvider)).ImplementationInstance as TimeProvider
                ?? throw new InvalidOperationException("The fixture has no fake clock.");
            timerClock = new BudgetTimerClock(fakeClock, SemanticQueryService.Budget);
            services.AddSingleton<TimeProvider>(timerClock);
        });
        var page = await f.PublishAsync("Práce z domova", "## Připojení zvenku\nPřes klienta a telefon.", null, ("Připojení zvenku", "zvenku"));
        await f.IndexAsync();
        var cache = f.Index.Host.Services.GetRequiredService<QueryEmbeddingCache>();
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Slow(TimeSpan.FromSeconds(2)));

        // The model takes 2 s: the search answers lexically once the 300 ms budget is over, without cancelling the call.
        timerClock!.ObserveBudgetTimer();
        var embedding = f.Semantic.TryEmbedAsync(Remote, new SearchOptions(), true, 0, default).AsTask();
        timerClock.StopObservingBudgetTimer();
        await WaitAsync(() => QueryCalls(f) == 1);
        // The service starts the call before it creates the budget's timer, so the recorded call does not say that the
        // 300 ms are running: advanced earlier, the fake clock would put the deadline at 599 ms and the wait would never end.
        await timerClock.BudgetTimerStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(embedding.IsCompleted);
        f.Index.Clock.Advance(TimeSpan.FromMilliseconds(299));
        await Task.Delay(50);
        Assert.False(embedding.IsCompleted);
        f.Index.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(await embedding.WaitAsync(TimeSpan.FromSeconds(10)));
        var lexical = await f.InvokeAsync(f.Reader, Remote, null);
        Assert.False(lexical.Semantic);
        Assert.Empty(lexical.Items);
        // An exhausted budget is not a failure of the profile.
        Assert.Equal((AiProfileState.Available, null), Health(f));

        // The late answer lands in the cache: the next pass uses it without calling the model again.
        f.Index.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitAsync(() => cache.Count == 1);
        var cached = await f.Semantic.TryEmbedAsync(Remote, new SearchOptions(), true, 0, default);
        Assert.NotNull(cached);
        Assert.Equal(f.Generation.Id, cached.GenerationId);
        Assert.Equal(1, QueryCalls(f));
        var hybrid = await f.FindAsync(f.Reader, Remote);
        Assert.True(hybrid.Semantic);
        Assert.Equal([page], hybrid.Items.Select(x => x.Id));
        Assert.Equal(1, QueryCalls(f));

        // A failing model: lexical at once, and the circuit breaker hears it; two in a row make the capability go away.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Connection));
        Assert.Null(await f.Semantic.TryEmbedAsync("jiný dotaz", new SearchOptions(), true, 0, default));
        Assert.Equal((AiProfileState.Available, AiOutcome.Connection), Health(f));
        var failed = await f.FindAsync(f.Reader, "domova");
        Assert.False(failed.Semantic);
        Assert.Equal([page], failed.Items.Select(x => x.Id));
        Assert.Equal(AiProfileState.Unavailable, Health(f).State);
        var calls = QueryCalls(f);
        Assert.Null(await f.Semantic.TryEmbedAsync("třetí dotaz", new SearchOptions(), true, 0, default));
        Assert.Equal(calls, QueryCalls(f));
    }

    [PostgreSqlFact]
    public async Task Prefix_pass_drafts_tags_and_missing_capability_never_call_the_provider()
    {
        await using var f = await HybridSearchFixture.CreateAsync();
        await f.PublishAsync("Práce z domova", "Text.");
        await f.IndexAsync();
        var semantic = f.Semantic;

        // The omnisearch prefix pass, the drafts scope, a query too short or too long, a request the handler rejects.
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(), false, 0, default));
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(Content: "drafts"), true, 0, default));
        Assert.Null(await semantic.TryEmbedAsync("pr", new SearchOptions(), true, 0, default));
        Assert.Null(await semantic.TryEmbedAsync(" ", new SearchOptions(), true, 0, default));
        Assert.Null(await semantic.TryEmbedAsync(new string('a', 201), new SearchOptions(), true, 0, default));
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(Content: "other"), true, 0, default));
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(), true, 1001, default));
        // (Search has no tag filter; the tag view is a separate listing and never searches.)
        Assert.Equal(0, QueryCalls(f));

        // The task switched off in Administrace → AI → Funkce, then the master switch off: the capability is gone, no call.
        var features = await f.Index.As<AiFeaturesView>(f.Admin, new Atlas.Server.Features.Ai.Settings.Queries.GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new SaveAiFeaturesRequest(
            [new SaveAiFeatureItem(AiTaskCatalog.SearchEmbedQuery, false, null)], features.Version)));
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(), true, 0, default));
        Assert.False((await f.FindAsync(f.Reader, "práce z domova")).Semantic);
        Assert.Equal(0, QueryCalls(f));
        await f.Index.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new SaveAiSwitchRequest(false, (await f.Index.As<AiSwitchView>(f.Admin,
            new Atlas.Server.Features.Ai.Settings.Queries.GetAiSwitch())).Version)));
        Assert.Null(await semantic.TryEmbedAsync("práce z domova", new SearchOptions(), true, 0, default));
        Assert.Equal(0, QueryCalls(f));
    }

    [PostgreSqlFact]
    public async Task Unavailable_embeddings_give_exactly_the_lexical_json()
    {
        await using var f = await HybridSearchFixture.CreateAsync(RemoteMeaning);
        await f.PublishAsync("Práce z domova", "## Připojení zvenku\nPřes klienta a telefon z domova.", null, ("Připojení zvenku", "zvenku"));
        await f.PublishAsync("Domovská stránka", "Rozcestník.");
        await f.IndexAsync();
        var lexical = JsonSerializer.Serialize(await f.InvokeAsync(f.Reader, "domova", null), JsonSerializerOptions.Web);

        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Http5xx));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(lexical, JsonSerializer.Serialize(await f.FindAsync(f.Reader, "domova"), JsonSerializerOptions.Web));
        }
        Assert.Equal(AiProfileState.Unavailable, Health(f).State);
        Assert.DoesNotContain("semantic", lexical, StringComparison.Ordinal);
        Assert.DoesNotContain("passage", lexical, StringComparison.Ordinal);
        Assert.DoesNotContain("didYouMean", lexical, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Answer_budget_waits_up_to_one_second_and_shares_the_search_cache()
    {
        BudgetTimerClock? timerClock = null;
        await using var f = await HybridSearchFixture.CreateAsync(RemoteMeaning, services =>
        {
            var fakeClock = services.Last(service => service.ServiceType == typeof(TimeProvider)).ImplementationInstance as TimeProvider
                ?? throw new InvalidOperationException("The fixture has no fake clock.");
            timerClock = new BudgetTimerClock(fakeClock, SemanticQueryService.AnswerBudget);
            services.AddSingleton<TimeProvider>(timerClock);
        });
        await f.PublishAsync("Práce z domova", "## Připojení zvenku\nPřes klienta a telefon.", null, ("Připojení zvenku", "zvenku"));
        await f.IndexAsync();

        // A question searched first (the omnisearch's full pass) is answered from the same vector: no second call.
        Assert.True((await f.FindAsync(f.Reader, Remote)).Semantic);
        Assert.Equal(1, QueryCalls(f));
        var shared = await f.Semantic.TryEmbedAsync(Remote, SemanticQueryService.AnswerBudget, default);
        Assert.NotNull(shared);
        Assert.Equal(f.Generation.Id, shared.GenerationId);
        Assert.Equal(1, QueryCalls(f));

        // A new question with a slow model: the answer waits its full second (not the 300 ms of search), then goes lexical.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Slow(TimeSpan.FromSeconds(3)));
        var question = "Jak se dostanu na " + Remote + " mimo kancelář?";
        timerClock!.ObserveBudgetTimer();
        var embedding = f.Semantic.TryEmbedAsync(question, SemanticQueryService.AnswerBudget, default).AsTask();
        timerClock.StopObservingBudgetTimer();
        await WaitAsync(() => QueryCalls(f) == 2);
        await timerClock!.BudgetTimerStarted.WaitAsync(TimeSpan.FromSeconds(10));
        f.Index.Clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(embedding.IsCompleted);
        f.Index.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(await embedding.WaitAsync(TimeSpan.FromSeconds(10)));
        // A question over 200 characters is embedded by its first 200, cut at a word, under the key of that text.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        var longQuestion = string.Join(' ', Enumerable.Repeat("Kde najdu postup pro vzdálený přístup?", 12));
        Assert.NotNull(await f.Semantic.TryEmbedAsync(longQuestion, SemanticQueryService.AnswerBudget, default));
        var input = f.Fake.Calls.Last(x => x.Task == AiTaskCatalog.SearchEmbedQuery).Inputs.Single();
        Assert.Equal(IndexFixture.Embeddings().QueryPrefix + longQuestion[..longQuestion.LastIndexOf(' ', 200)], input);
        // Too short to mean anything: no call.
        var calls = QueryCalls(f);
        Assert.Null(await f.Semantic.TryEmbedAsync("ok", SemanticQueryService.AnswerBudget, default));
        Assert.Equal(calls, QueryCalls(f));
    }

    /// <summary>Signals only after Task.Delay has registered the given budget with the shared fake clock.</summary>
    private sealed class BudgetTimerClock(TimeProvider inner, TimeSpan budget) : TimeProvider
    {
        private readonly TaskCompletionSource m_budgetTimerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncLocal<bool> m_observeBudget = new();

        public Task BudgetTimerStarted => m_budgetTimerStarted.Task;
        public void ObserveBudgetTimer() => m_observeBudget.Value = true;
        public void StopObservingBudgetTimer() => m_observeBudget.Value = false;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (m_observeBudget.Value && dueTime == budget)
            {
                m_budgetTimerStarted.TrySetResult();
            }
            return timer;
        }
    }

    private static (AiProfileState State, string? LastErrorCode) Health(HybridSearchFixture f)
    {
        var health = f.Index.Availability.Health(AiProfileKind.Embeddings);
        return (health.State, health.LastErrorCode);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }
}
