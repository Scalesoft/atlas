using System.Text.Json;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Prompts;
using Microsoft.Extensions.AI;
using Wolverine;

namespace Atlas.Server.Features.KnowledgeMaintenance;

public sealed record AiEvaluationReport(string SetVersion, string PromptVersion, IReadOnlyList<AiEvaluationRow> Items);
public sealed record AiEvaluationRow(string Id, string Question, bool ExpectedAnswerable, bool? Answerable, bool Passed, int InvalidCitations, string? Code, int LatencyMs);
internal sealed record AiEvaluationCase(string Id, string Question, bool Answerable, IReadOnlyList<AnswerPromptSource> Sources, IReadOnlyList<int> ExpectedCitations);
internal sealed record AiEvaluationSet(string Version, IReadOnlyList<AiEvaluationCase> Cases);

/// <summary>Explicit synthetic diagnostic, not a retrieval or factual-quality score. Model calls run between short
/// authorization transactions. Results contain no generated text and are never persisted.</summary>
public sealed class AiEvaluationRunner(IMessageBus bus, IAiClientFactory factory, TimeProvider clock)
{
    private static readonly AiEvaluationSet s_set = LoadSet();

    public async Task<AiEvaluationReport> RunAsync(CancellationToken ct)
    {
        var profile = await bus.InvokeAsync<AiProfileSnapshot>(new BeginAiProfileTest(AiProfileKind.Chat), ct);
        var rows = new List<AiEvaluationRow>();
        foreach (var item in s_set.Cases)
        {
            await bus.InvokeAsync(new CompleteAiProfileTest(profile.Kind, profile.RevisionId), ct);
            rows.Add(await EvaluateAsync(profile, item, ct));
            await bus.InvokeAsync(new CompleteAiProfileTest(profile.Kind, profile.RevisionId), ct);
        }
        return new AiEvaluationReport(s_set.Version, AnswerPrompt.Version, rows);
    }

    private async Task<AiEvaluationRow> EvaluateAsync(AiProfileSnapshot profile, AiEvaluationCase item, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        try
        {
            using var chat = factory.Chat(profile, AiTaskCatalog.AdminEval);
            var response = await chat.GetResponseAsync<AnswerResult>(AnswerPromptBuilder.Build(item.Question, item.Sources),
                JsonSerializerOptions.Web, new ChatOptions { MaxOutputTokens = 1200 }, cancellationToken: ct);
            if (!response.TryGetResult(out var answer))
            {
                return Failed(AiOutcome.InvalidOutput);
            }
            var validated = AnswerValidator.Validate(answer, item.Sources.Count);
            var passed = answer.Answerable == item.Answerable && validated.Answerable == item.Answerable
                && validated.InvalidCitations == 0 && validated.DroppedSentences == 0
                && item.ExpectedCitations.All(validated.Cited.Contains);
            return new AiEvaluationRow(item.Id, item.Question, item.Answerable, answer.Answerable, passed,
                validated.InvalidCitations, null, Latency());
        }
        catch (AiCallException exception) { return Failed(exception.Code); }
        catch (AiConfigurationException) { return Failed(AiOutcome.BadRequest); }

        int Latency() => (int)Math.Clamp(Math.Round(clock.GetElapsedTime(started).TotalMilliseconds), 0, int.MaxValue);
        AiEvaluationRow Failed(string code) => new(item.Id, item.Question, item.Answerable, null, false, 0, code, Latency());
    }

    private static AiEvaluationSet LoadSet()
    {
        using var stream = typeof(AiEvaluationRunner).Assembly.GetManifestResourceStream("Atlas.Server.Features.KnowledgeMaintenance.answer-diagnostic.v1.json")
            ?? throw new InvalidOperationException("Embedded answer diagnostic is missing.");
        return JsonSerializer.Deserialize<AiEvaluationSet>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Embedded answer diagnostic is empty.");
    }
}
