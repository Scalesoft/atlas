using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Tests.Assistant;

/// <summary>The registry's operations each in its own Wolverine transaction, as the answer flow (Q3, Q4) calls them: the
/// retrieval after <c>LockActorAsync</c> of the scope's space, the gate's authorization, text and status reads.</summary>
public sealed record RetrieveAnswerSources(string Question, AnswerScope Scope, SemanticQuery? Vector, int Take = 6, int PerSource = 2);

public sealed record LoadAnswerSourceTexts(IReadOnlyList<SourceReference> References);

public sealed record AuthorizeAnswerSources(IReadOnlyList<SourceReference> References, bool RequireCurrentRevision);

public sealed record ReadAnswerRetrievalSettings(string Question, SemanticQuery Vector);

public sealed record ReadAnswerSourceStatus(IReadOnlyList<SourceReference> References);

public sealed class AnswerSourceProbeHandler(IContentAccess access, SourceRegistry registry, NHibernate.ISession session)
{
    public async Task<AnswerSourceSet> Handle(RetrieveAnswerSources message, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(message.Scope.SpaceId, ct);
        return await registry.RetrieveAsync(new AnswerRetrievalRequest(actor, message.Question, message.Scope, message.Vector, message.Take,
            message.PerSource), ct);
    }

    public async Task<IReadOnlyList<string>> Handle(ReadAnswerRetrievalSettings message, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        await registry.RetrieveAsync(new AnswerRetrievalRequest(actor, message.Question, AnswerScope.Wiki, message.Vector, 6, 2), ct);
        var settings = await session.CreateSQLQuery("""
            SELECT current_setting('hnsw.iterative_scan') AS scan, current_setting('hnsw.ef_search') AS breadth, current_setting('hnsw.max_scan_tuples') AS tuples
            """).UniqueResultAsync<object[]>(ct);
        return settings.Select(x => (string)x).ToArray();
    }

    public Task<IReadOnlyDictionary<SourceReference, string?>> Handle(LoadAnswerSourceTexts message, CancellationToken ct)
        => registry.LoadTextAsync(message.References, ct);

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> Handle(AuthorizeAnswerSources message, CancellationToken ct)
        => registry.AuthorizeAsync(message.References, message.RequireCurrentRevision, ct);

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> Handle(ReadAnswerSourceStatus message, CancellationToken ct)
        => registry.StatusAsync(message.References, ct);
}
