using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

public sealed class PageSummaryTests
{
    [Fact]
    public async Task Summary_prepares_document_sections_without_question_ranking_and_persists_coverage()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        var text = string.Join("\n\n", Enumerable.Range(1, 14).Select(n => "## Sekce " + n + "\n\n" + string.Join(" ", Enumerable.Repeat("Obsah odstavce sekce " + n + ".", 110))));
        var page = await f.PublishAsync("Dlouhý dokument", text, null, Enumerable.Range(1, 14).Select(n => ("Sekce " + n, "s" + n)).ToArray());
        await f.IndexAsync();
        f.Fake.ChatAnswer = Atlas.Server.Tests.Ai.Fakes.FakeAi.Answers.Citing(1);
        var prepared = await f.RunAsync(f.Reader, runner => runner.SummaryAsync(new PageSummaryRequest(page), default));
        Assert.True(prepared.Truncated);
        Assert.True(prepared.Sources.Count > 6);
        var answer = await f.CompleteAsync(f.Reader, prepared.MessageId);
        Assert.True(answer.Truncated);
        Assert.Equal(AiMessageState.Answered, answer.State);
        var stored = await f.StoredAsync(prepared.MessageId);
        Assert.Equal("page.summary", stored.Message.Task);
        Assert.True(stored.Message.SourceCoverageTruncated);
        Assert.All(stored.Dependencies, x => Assert.Equal(page, x.PageId));
        var call = Assert.Single(f.ChatCalls);
        Assert.Equal(1200, call.ChatOptions!.MaxOutputTokens);
        Assert.Equal("summary@1", stored.Message.PromptVersion);
        Assert.True(call.Inputs.Sum(x => x.Length) <= SummaryPrompt.Budget);
        Assert.All(Enumerable.Range(1, 7), number => Assert.Contains(prepared.Sources, x => x.HeadingId == "s" + number));
        var fresh = await f.Index.As<AiConversationView>(f.Reader, new Atlas.Server.Features.Assistant.Queries.GetAiConversation(prepared.ConversationId));
        Assert.True(fresh.Messages.Single(x => x.Role == AiMessageRole.Assistant).Truncated);
    }
    [PostgreSqlFact]
    public async Task Summary_without_sources_or_with_model_abstention_does_not_record_a_search_gap()
    {
        await using var f = await AnswerFlowFixture.CreateAsync();
        var indexed = await f.PublishAsync("Shrnovaná stránka", "## Postup\nProveďte běžný postup.", null, ("Postup", "postup"));
        await f.IndexAsync();
        var notIndexed = await f.PublishAsync("Nová publikace", "Obsah ještě není v indexu.");
        var misses = f.Index.Host.Services.GetRequiredService<Atlas.Server.Features.SearchGaps.SearchMissRecorder>();
        misses.Drain(f.Index.Clock.GetUtcNow(), all: true);
        var absent = await f.RunAsync(f.Reader, runner => runner.SummaryAsync(new PageSummaryRequest(notIndexed), default));
        Assert.Equal(AiMessageState.NoSources, absent.State);
        Assert.Empty(f.ChatCalls);
        Assert.Empty(misses.Drain(f.Index.Clock.GetUtcNow().AddMinutes(1), all: true));
        f.Fake.ChatAnswer = Atlas.Server.Tests.Ai.Fakes.FakeAi.Answers.Unanswerable;
        var prepared = await f.RunAsync(f.Reader, runner => runner.SummaryAsync(new PageSummaryRequest(indexed), default));
        Assert.Equal(AiMessageState.NoSources, (await f.CompleteAsync(f.Reader, prepared.MessageId)).State);
        Assert.Empty(misses.Drain(f.Index.Clock.GetUtcNow().AddMinutes(1), all: true));
    }
}
