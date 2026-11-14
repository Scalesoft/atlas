using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiConcurrencyGateTests
{
    [Fact]
    public async Task Indexing_never_takes_the_interactive_slot_and_a_busy_interactive_call_does_not_wait()
    {
        var gate = new AiConcurrencyGate();
        var profile = FakeAi.Profile(AiProfileKind.Embeddings) with { MaxConcurrency = 3 };
        var first = await gate.EnterAsync(profile, AiTaskCatalog.IndexEmbed, default);
        var second = await gate.EnterAsync(profile, AiTaskCatalog.IndexEmbed, default);
        // Indexing may hold max(1, 3 − 1) slots; the third indexing call queues.
        var third = gate.EnterAsync(profile, AiTaskCatalog.IndexEmbed, default).AsTask();
        Assert.False(third.IsCompleted);

        var search = await gate.EnterAsync(profile, AiTaskCatalog.SearchEmbedQuery, default);
        // Full: an interactive call answers at once instead of waiting (search then stays lexical).
        var busy = gate.EnterAsync(profile, AiTaskCatalog.EditorLink, default).AsTask();
        Assert.True(busy.IsCompleted);
        Assert.Equal(AiOutcome.Busy, (await Assert.ThrowsAsync<AiCallException>(() => busy)).Code);

        // The freed interactive slot stays interactive.
        search.Dispose();
        await Task.Yield();
        Assert.False(third.IsCompleted);
        first.Dispose();
        using (await third)
        {
            using (await gate.EnterAsync(profile, AiTaskCatalog.PageCreateSuggest, default))
            {
            }
        }
        second.Dispose();

        // Through the pipeline: a busy interactive call neither reaches the model nor counts as usage or a failure.
        using var ai = new AiPipelineTests.PipelineHost();
        var single = FakeAi.Profile(AiProfileKind.Embeddings) with { MaxConcurrency = 1 };
        using var generator = ai.Fake.Embeddings(single, AiTaskCatalog.SearchEmbedQuery);
        var hold = ai.Fake.Hold(AiProfileKind.Embeddings);
        var running = generator.GenerateAsync(["první dotaz"]);
        await hold.Entered;
        var rejected = await Assert.ThrowsAsync<AiCallException>(() => generator.GenerateAsync(["druhý dotaz"]));
        Assert.Equal(AiOutcome.Busy, rejected.Code);
        hold.Release();
        await running;
        Assert.Single(ai.Fake.Calls);
        Assert.Equal([AiOutcome.Ok], ai.Usage.Drain().Select(x => x.Outcome));
        Assert.Equal([(single.RevisionId, AiOutcome.Ok)], ai.Outcomes.Reported);
        Assert.Equal(1, ai.Fake.MaxConcurrent(AiProfileKind.Embeddings));
    }
}
