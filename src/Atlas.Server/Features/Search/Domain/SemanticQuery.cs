using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Search.Contracts;

namespace Atlas.Server.Features.Search.Domain;

/// <summary>A query embedded for one index generation: the hybrid search compares it only with that generation's chunks, and
/// only while the generation is still Active (one snapshot of the pair, spec RAG §6).</summary>
public sealed record SemanticQuery(Guid GenerationId, float[] Vector);

/// <summary>
/// S1/S2: the query vector of a search, made by the endpoint before the handler runs, so no model call ever happens inside a
/// handler's transaction or under the actor lock (plan §1.1). Never throws for AI reasons: null means "search lexically".
/// </summary>
public interface ISemanticQuery
{
    /// <summary>Null without a model call when the semantic-search capability is off (memory only, AI-24), for the prefix pass
    /// (<paramref name="fuzzy"/> false), for drafts, for a request the handler will reject, and for a normalized query shorter
    /// than 3 or longer than 200 characters. Otherwise the cached vector, or a new embedding awaited for at most
    /// <see cref="SemanticQueryService.Budget"/>; a later answer is cached for the next pass or page.</summary>
    ValueTask<SemanticQuery?> TryEmbedAsync(string? query, SearchOptions options, bool fuzzy, int offset, CancellationToken ct);

    /// <summary>The question of an answer from the wiki (plan phase 3 R3): always the full pass, no search options, awaited for at
    /// most <paramref name="budget"/> (<see cref="SemanticQueryService.AnswerBudget"/>), otherwise as above: the same capability,
    /// cache, shared call and interactive slot, so a question first searched in the omnisearch costs no second call. A question
    /// longer than 200 characters is embedded by its first 200 (cut at a word). Null means "retrieve lexically".</summary>
    ValueTask<SemanticQuery?> TryEmbedAsync(string query, TimeSpan budget, CancellationToken ct);
}

/// <summary>
/// The production <see cref="ISemanticQuery"/> (plan §11, R4). The embedding runs on the interactive slot of the embeddings
/// profile (<see cref="AiConcurrencyGate"/>: a busy profile rejects at once and the search stays lexical) and is never
/// cancelled when the budget runs out: it finishes within the profile timeout and lands in <see cref="QueryEmbeddingCache"/>.
/// An exhausted budget is not a failure of the profile; a failed call is (the pipeline reports it to the circuit breaker).
/// Neither the query nor its vector is logged.
/// </summary>
public sealed class SemanticQueryService(AiConfigurationCache configuration, AiCapabilities capabilities, IAiClientFactory factory,
    QueryEmbeddingCache cache, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<SemanticQueryService> logger, Atlas.Server.Features.Ai.Access.AiUserAccess access) : ISemanticQuery
{
    /// <summary>AI-25: how long a search waits for its query vector before it answers lexically.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    /// <summary>Plan phase 3 R3: an answer waits seconds for its model anyway, so its question vector may take a second.</summary>
    public static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(1);

    /// <summary>The longest text that is embedded as a query (the length limit of a search).</summary>
    public const int MaxQueryCharacters = 200;

    public async ValueTask<SemanticQuery?> TryEmbedAsync(string? query, SearchOptions options, bool fuzzy, int offset, CancellationToken ct)
    {
        if (!fuzzy || options.Content is not ("all" or "published") || offset is < 0 or > 1000 || query is null || query.Length > MaxQueryCharacters)
        {
            return null;
        }
        return await EmbedWithinAsync(query.Trim(), SearchText.Normalize(query), Budget, ct);
    }

    public async ValueTask<SemanticQuery?> TryEmbedAsync(string query, TimeSpan budget, CancellationToken ct)
    {
        var text = Shorten(query ?? "");
        return await EmbedWithinAsync(text, Shorten(SearchText.NormalizeContent(text)), budget, ct);
    }

    /// <summary>The first <see cref="MaxQueryCharacters"/> characters, cut at a word when one ends in the second half. A text within
    /// the limit is only trimmed, so a question that was searched first keeps that search's cache key.</summary>
    private static string Shorten(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxQueryCharacters)
        {
            return text;
        }
        var cut = text.LastIndexOf(' ', MaxQueryCharacters);
        return (cut > MaxQueryCharacters / 2 ? text[..cut] : text[..MaxQueryCharacters]).TrimEnd();
    }

    private async ValueTask<SemanticQuery?> EmbedWithinAsync(string text, string normalized, TimeSpan budget, CancellationToken ct)
    {
        if (normalized.Length is < 3 or > MaxQueryCharacters)
        {
            return null;
        }
        var snapshot = await configuration.GetAsync(ct);
        if (!capabilities.Allows(snapshot, AiTaskCatalog.SearchEmbedQuery)
            || snapshot.ActiveGeneration is not { } generation || snapshot.Profile(AiProfileKind.Embeddings) is not { } profile)
        {
            return null;
        }
        if (!await access.AllowedAsync(ct)) return null;
        var key = new QueryEmbeddingKey(generation.Id, normalized);
        if (cache.TryGet(key, out var cached))
        {
            return new SemanticQuery(generation.Id, cached);
        }
        var pending = cache.GetOrStart(key, () => EmbedAsync(profile, text));
        if (!pending.IsCompleted)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var expired = Task.Delay(budget, clock, waiting.Token);
            await Task.WhenAny(pending, expired);
            await waiting.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
        return pending.IsCompletedSuccessfully && pending.Result is { } vector ? new SemanticQuery(generation.Id, vector) : null;
    }

    /// <summary>Runs on its own, not on the request's token: an answer after the budget still fills the cache.</summary>
    private async Task<float[]?> EmbedAsync(AiProfileSnapshot profile, string query)
    {
        try
        {
            using var generator = factory.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery);
            var embeddings = await generator.GenerateAsync([profile.Prepare(query, AiEmbeddingPurpose.Query)], cancellationToken: lifetime.ApplicationStopping);
            return embeddings[0].Vector.ToArray();
        }
        catch (Exception exception) when (exception is AiCallException or AiConfigurationException or OperationCanceledException)
        {
            // Busy, an outage or an unusable answer: the pipeline has already told the circuit breaker what counts; search
            // answers lexically and the next search tries again.
            return null;
        }
        catch (Exception exception)
        {
            // Never the query: only what failed. Search still answers lexically.
            logger.LogWarning("Embedding a search query failed unexpectedly ({ErrorType}); the search stays lexical", exception.GetType().Name);
            return null;
        }
    }
}

public readonly record struct QueryEmbeddingKey(Guid GenerationId, string NormalizedQuery);

/// <summary>
/// Query vectors in process memory (R4): keyed by the generation and the normalized query, kept 10 minutes and at most 2 000,
/// least recently used out first. A query being embedded is shared, so a burst of the same search (the omnisearch passes,
/// the next page, another reader) makes one model call. A new generation never meets an old vector: it is in the key.
/// </summary>
public sealed class QueryEmbeddingCache(TimeProvider clock)
{
    public const int Capacity = 2000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly Lock m_lock = new();
    private readonly Dictionary<QueryEmbeddingKey, LinkedListNode<Entry>> m_entries = new();
    private readonly LinkedList<Entry> m_order = new();
    private readonly Dictionary<QueryEmbeddingKey, Task<float[]?>> m_pending = new();

    public int Count
    {
        get
        {
            lock (m_lock)
            {
                return m_entries.Count;
            }
        }
    }

    public bool TryGet(QueryEmbeddingKey key, out float[] vector)
    {
        lock (m_lock)
        {
            if (m_entries.TryGetValue(key, out var node))
            {
                if (clock.GetUtcNow() - node.Value.StoredAt < Lifetime)
                {
                    m_order.Remove(node);
                    m_order.AddFirst(node);
                    vector = node.Value.Vector;
                    return true;
                }
                m_order.Remove(node);
                m_entries.Remove(key);
            }
        }
        vector = [];
        return false;
    }

    /// <summary>The embedding of the key in flight, or a new one from <paramref name="start"/>; a vector it produces is
    /// stored when it completes, whoever is still waiting.</summary>
    public Task<float[]?> GetOrStart(QueryEmbeddingKey key, Func<Task<float[]?>> start)
    {
        Task<float[]?> task;
        lock (m_lock)
        {
            if (m_pending.TryGetValue(key, out var running))
            {
                if (!running.IsCompleted)
                {
                    return running;
                }
                // Finished, and its continuation has not run yet: settle it here. A failure is not kept; the caller starts anew.
                m_pending.Remove(key);
                Complete(key, running);
                if (running.IsCompletedSuccessfully && running.Result is not null)
                {
                    return running;
                }
            }
            task = start();
            if (task.IsCompleted)
            {
                Complete(key, task);
                return task;
            }
            m_pending[key] = task;
        }
        _ = task.ContinueWith(done =>
        {
            lock (m_lock)
            {
                if (m_pending.TryGetValue(key, out var current) && current == done)
                {
                    m_pending.Remove(key);
                    Complete(key, done);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void Complete(QueryEmbeddingKey key, Task<float[]?> task)
    {
        if (!task.IsCompletedSuccessfully || task.Result is not { } vector)
        {
            return;
        }
        if (m_entries.Remove(key, out var existing))
        {
            m_order.Remove(existing);
        }
        m_entries[key] = m_order.AddFirst(new Entry(key, vector, clock.GetUtcNow()));
        while (m_entries.Count > Capacity && m_order.Last is { } oldest)
        {
            m_order.RemoveLast();
            m_entries.Remove(oldest.Value.Key);
        }
    }

    private sealed record Entry(QueryEmbeddingKey Key, float[] Vector, DateTimeOffset StoredAt);
}
