using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// A cross-encoder reranker (bge-reranker-v2-m3 class): the relevance of each candidate source to the question, higher first. Called
/// outside any transaction and lock, after <c>AiGate</c> allowed the answer, over candidates the actor may read.
/// </summary>
public interface IAnswerReranker
{
    /// <summary>Whether a reranker is configured and available now, from memory only; false skips the rerank step and its extra read.</summary>
    ValueTask<bool> AvailableAsync(CancellationToken ct);

    /// <summary>One score per document, in the documents' order. Throws on any failure; <see cref="AnswerRerank.OrderAsync"/> turns every
    /// failure and an exhausted budget into today's order.</summary>
    Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct);
}

/// <summary>
/// The production reranker: the rerank profile (Administrace → AI) of the task <c>search.rerank</c>. Available while
/// <see cref="AiCapabilities"/> allows the task (the AI switch, the task, an enabled profile the circuit breaker holds available), from
/// memory; scored through the AI pipeline (the profile's slot, usage, timeout, circuit breaker).
/// </summary>
public sealed class ProfileAnswerReranker(AiConfigurationCache configuration, AiCapabilities capabilities, IAiClientFactory factory) : IAnswerReranker
{
    public async ValueTask<bool> AvailableAsync(CancellationToken ct)
        => capabilities.Allows(await configuration.GetAsync(ct), AiTaskCatalog.SearchRerank);

    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        var profile = (await configuration.GetAsync(ct)).ProfileFor(AiTaskCatalog.SearchRerank)
            ?? throw new AiConfigurationException("No rerank profile.");
        return await factory.Rerank(profile, AiTaskCatalog.SearchRerank).ScoreAsync(query, documents, ct);
    }
}

/// <summary>
/// The optional rerank step of answer sources: the pool of <see cref="Depth"/> candidate chunks scored within <see cref="Budget"/>. An
/// unavailable reranker, a slow one, a failure or an unusable answer give null, and the answer keeps today's sources. Measured on the
/// real wiki (2026-10-20, bge-reranker-v2-m3): the expected page among an answer's 6 sources 77.9 % → 89.4 %. Search and the agent's
/// search are not reranked: there it gained nothing (search MRR 0.897 → 0.885, +200 ms). Neither the question nor a document is logged.
/// </summary>
public sealed class AnswerRerank(IAnswerReranker reranker, TimeProvider clock, ILogger<AnswerRerank> logger)
{
    /// <summary>The candidate chunks one rerank scores.</summary>
    public const int Depth = 30;

    /// <summary>How long the preparation of an answer waits for the scores (30 chunks took about 0.3–0.55 s on one GPU).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    public ValueTask<bool> AvailableAsync(CancellationToken ct) => reranker.AvailableAsync(ct);

    /// <summary>The documents' indexes best first (a tie keeps today's order), or null for today's order.</summary>
    public async Task<IReadOnlyList<int>?> OrderAsync(string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        if (documents.Count < 2 || !await reranker.AvailableAsync(ct))
        {
            return null;
        }
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IReadOnlyList<double>>? scoring = null;
        try
        {
            scoring = reranker.ScoreAsync(query, documents, call.Token);
            // The budget wins even over a provider that ignores cancellation.
            var scores = await scoring.WaitAsync(Budget, clock, ct);
            return scores.Count == documents.Count && scores.All(double.IsFinite)
                ? Enumerable.Range(0, documents.Count).OrderByDescending(index => scores[index]).ThenBy(index => index).ToArray()
                : null;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // Scores after the budget are of no use: stop the call, and observe its end so it is never an unobserved exception.
            await call.CancelAsync();
            _ = scoring?.ContinueWith(task => task.Exception, TaskScheduler.Default);
            // The pipeline has logged a failed call already; an exhausted budget is no failure.
            if (exception is not (TimeoutException or AiCallException))
            {
                logger.LogWarning("Reranking failed ({ErrorType}); today's order stays", exception.GetType().Name);
            }
            return null;
        }
    }

    /// <summary>The text a candidate is scored by: its title, its section and its text.</summary>
    public static string Document(string title, string? heading, string text)
        => heading is { Length: > 0 } ? $"{title} › {heading}\n{text}" : $"{title}\n{text}";
}
