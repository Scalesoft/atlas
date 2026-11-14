namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// The registered source adapters (AI-11): in production only <see cref="PageSourceAdapter"/>, a test host may add a synthetic
/// one. Not a plugin platform: a list from DI. The common flow calls this class and never branches on a source type; a stored
/// reference whose type or version no adapter serves is <see cref="SourceStatus.Unavailable"/> and has no text (spec RAG §1).
/// Scoped: the adapters share the transaction's session.
/// </summary>
public sealed class SourceRegistry
{
    private readonly IReadOnlyList<IKnowledgeSourceAdapter> m_adapters;

    public SourceRegistry(IEnumerable<IKnowledgeSourceAdapter> adapters)
    {
        m_adapters = adapters.ToArray();
        var duplicate = m_adapters.GroupBy(adapter => (adapter.Type, adapter.Version)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two source adapters serve {duplicate.Key.Type} version {duplicate.Key.Version}.");
        }
    }

    public IKnowledgeSourceAdapter? Find(string type, int version)
        => m_adapters.FirstOrDefault(adapter => adapter.Type == type && adapter.Version == version);

    /// <summary>Every adapter's retrieval, merged by <see cref="AuthorizedSource.Rank"/> (higher first, an adapter's own order
    /// kept on ties) and cut to <see cref="AnswerRetrievalRequest.Take"/>. Hybrid when the endpoint brought a vector: the SQL
    /// ignores a vector whose generation is no longer active, which then only mislabels the diagnostics of one message.</summary>
    public async Task<AnswerSourceSet> RetrieveAsync(AnswerRetrievalRequest request, CancellationToken ct)
    {
        var all = new List<(AuthorizedSource Source, int Adapter, int Position)>();
        if (request.Take > 0)
        {
            for (var index = 0; index < m_adapters.Count; index++)
            {
                // One after another: the adapters share the transaction's connection.
                var sources = await m_adapters[index].RetrieveAsync(request, ct);
                all.AddRange(sources.Select((source, position) => (source, index, position)));
            }
        }
        var merged = all.OrderByDescending(x => x.Source.Rank).ThenBy(x => x.Adapter).ThenBy(x => x.Position)
            .Take(Math.Max(0, request.Take)).Select(x => x.Source).ToArray();
        return new AnswerSourceSet(merged, request.Vector is null ? AnswerRetrieval.Lexical : AnswerRetrieval.Hybrid);
    }

    public Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
        => DispatchAsync(refs, (adapter, mine) => adapter.LoadTextAsync(mine, ct), (string?)null);

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(IReadOnlyList<SourceReference> refs, bool requireCurrentRevision,
        CancellationToken ct)
        => DispatchAsync(refs, (adapter, mine) => adapter.AuthorizeAsync(mine, requireCurrentRevision, ct), SourceStatus.Unavailable);

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
        => DispatchAsync(refs, (adapter, mine) => adapter.StatusAsync(mine, ct), SourceStatus.Unavailable);

    /// <summary>The view of references already found Available or Changed; an unknown type or version has nothing to show.</summary>
    public Task<IReadOnlyDictionary<SourceReference, SourceDescription?>> DescribeAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
        => DispatchAsync(refs, (adapter, mine) => adapter.DescribeAsync(mine, ct), (SourceDescription?)null);

    /// <summary>The typed dependency columns of a retrieved source; it came from a registered adapter.</summary>
    public SourceColumns Columns(AuthorizedSource source)
        => (Find(source.Reference.Type, source.Reference.Version)
            ?? throw new InvalidOperationException($"No source adapter serves {source.Reference.Type} version {source.Reference.Version}."))
            .Columns(source);

    /// <summary>The reference of stored dependency columns; null for an unknown type or version or columns that no longer name a
    /// source. The caller treats null as <see cref="SourceStatus.Unavailable"/>.</summary>
    public SourceReference? Reference(SourceColumns columns) => Find(columns.SourceType, columns.SourceVersion)?.Reference(columns);

    private async Task<IReadOnlyDictionary<SourceReference, T>> DispatchAsync<T>(IReadOnlyList<SourceReference> refs,
        Func<IKnowledgeSourceAdapter, IReadOnlyList<SourceReference>, Task<IReadOnlyDictionary<SourceReference, T>>> call, T refused)
    {
        var result = new Dictionary<SourceReference, T>();
        foreach (var group in refs.Distinct().GroupBy(reference => (reference.Type, reference.Version)))
        {
            var adapter = Find(group.Key.Type, group.Key.Version);
            var mine = group.ToArray();
            var answers = adapter is null ? null : await call(adapter, mine);
            foreach (var reference in mine)
            {
                // An adapter that leaves a reference out has not vouched for it.
                result[reference] = answers is not null && answers.TryGetValue(reference, out var answer) ? answer : refused;
            }
        }
        return result;
    }
}
