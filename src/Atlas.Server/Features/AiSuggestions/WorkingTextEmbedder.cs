using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>The vectors of one request's working texts, for the generation they were made for; a text whose vector did not
/// arrive within the budget, or failed, has none.</summary>
public sealed record WorkingTextVectors(Guid GenerationId, IReadOnlyList<float[]?> Vectors)
{
    public bool Any => Vectors.Any(x => x is not null);
}

/// <summary>
/// Embeds an author's working text (E2 phrases, the E4 section, the title of a P1–P3 page being created) for the endpoints,
/// before any handler runs, so no model call ever happens inside a transaction or under a lock (plan §1.1, §13). The rules of
/// <see cref="SemanticQueryService"/>: memory only to decide (AI-24), the interactive slot of the embeddings profile, at most
/// <see cref="SemanticQueryService.Budget"/> of waiting, a late answer kept in <see cref="QueryEmbeddingCache"/> for the next
/// request (the key is the generation and the normalized text, the text embedded with the query prefix, so it is the same
/// vector a search for that text would use). Never throws for AI reasons and never logs a text or a vector.
/// </summary>
/// <remarks>Z2/D3: a working-text task reaches a cloud profile only with the admin's permission. The capability already says
/// so (<see cref="AiCapabilities.MaySendInput"/>); this class checks it again against the very profile it calls.</remarks>
public sealed class WorkingTextEmbedder(AiConfigurationCache configuration, AiCapabilities capabilities, IAiClientFactory factory,
    QueryEmbeddingCache cache, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<WorkingTextEmbedder> logger, Atlas.Server.Features.Ai.Access.AiUserAccess access)
{
    /// <summary>Whether the task may embed now (capability: switch, task, profile, availability, generation, cloud permission).</summary>
    public async ValueTask<bool> AvailableAsync(string task, CancellationToken ct)
        => await capabilities.IsAvailableAsync(task, ct) && await access.AllowedAsync(ct);

    /// <summary>Null without any model call when the task may not run (or nothing is to be embedded); otherwise one vector slot
    /// per text, in order.</summary>
    public async ValueTask<WorkingTextVectors?> EmbedAsync(string task, IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0 || AiTaskCatalog.Find(task) is not { Input: AiInputClass.WorkingText } definition)
        {
            return null;
        }
        var snapshot = await configuration.GetAsync(ct);
        if (!capabilities.Allows(snapshot, task) || snapshot.ActiveGeneration is not { } generation
            || snapshot.Profile(AiProfileKind.Embeddings) is not { } profile || !AiCapabilities.MaySendInput(snapshot, definition, profile))
        {
            return null;
        }
        if (!await access.AllowedAsync(ct)) return null;
        var keys = texts.Select(text => new QueryEmbeddingKey(generation.Id, SearchText.NormalizeContent(text))).ToArray();
        return new WorkingTextVectors(generation.Id, await CachedVectorsAsync(profile, task, texts, keys, ct));
    }

    private async Task<float[]?[]> CachedVectorsAsync(AiProfileSnapshot profile, string task, IReadOnlyList<string> texts,
        IReadOnlyList<QueryEmbeddingKey> keys, CancellationToken ct)
    {
        var vectors = new float[]?[texts.Count];
        var pending = new Task<float[]?>?[texts.Count];
        var missing = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (cache.TryGet(keys[i], out var cached))
            {
                vectors[i] = cached;
            }
            else
            {
                missing.Add(i);
            }
        }
        if (missing.Count > 0)
        {
            // One call for every text not cached yet; each key shares it, so a concurrent request for the same text waits for
            // this call instead of making its own.
            var batch = new Lazy<Task<float[]?[]>>(() => EmbedAsync(profile, task, missing.Select(i => texts[i].Trim()).ToArray()));
            for (var m = 0; m < missing.Count; m++)
            {
                var position = m;
                pending[missing[m]] = cache.GetOrStart(keys[missing[m]], () => Pick(batch.Value, position));
            }
            var waiting = pending.Where(x => x is { IsCompleted: false }).Cast<Task>().ToArray();
            if (waiting.Length > 0)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var expired = Task.Delay(SemanticQueryService.Budget, clock, budget.Token);
                await Task.WhenAny(Task.WhenAll(waiting), expired);
                await budget.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            foreach (var i in missing)
            {
                vectors[i] = pending[i] is { IsCompletedSuccessfully: true, Result: { } vector } ? vector : null;
            }
        }
        return vectors;
    }

    private static async Task<float[]?> Pick(Task<float[]?[]> batch, int position) => (await batch)[position];

    /// <summary>Runs on its own, not on the request's token: an answer after the budget still fills the cache.</summary>
    private async Task<float[]?[]> EmbedAsync(AiProfileSnapshot profile, string task, string[] texts)
    {
        try
        {
            using var generator = factory.Embeddings(profile, task);
            var embeddings = await generator.GenerateAsync(texts.Select(text => profile.Prepare(text, AiEmbeddingPurpose.Query)),
                cancellationToken: lifetime.ApplicationStopping);
            return embeddings.Select(x => (float[]?)x.Vector.ToArray()).ToArray();
        }
        catch (Exception exception) when (exception is AiCallException or AiConfigurationException or OperationCanceledException)
        {
            // Busy, an outage or an unusable answer: the pipeline has told the circuit breaker what counts; the suggestion
            // keeps its rule-based part (or has nothing to offer) and the next request tries again.
            return new float[]?[texts.Length];
        }
        catch (Exception exception)
        {
            // Never the text: only what failed.
            logger.LogWarning("Embedding working text failed unexpectedly ({ErrorType}); the suggestion stays without its vector part",
                exception.GetType().Name);
            return new float[]?[texts.Length];
        }
    }
}
