using System.Text;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Assistant.Queries;

/// <summary>
/// The rerank pool of an answer (<see cref="AnswerRetrievalRequest.Pool"/>), read by the endpoint in its own short transaction before
/// the preparation, so the reranker is called outside any transaction and lock. Only what the actor may read (ACL, publication and G5 in
/// the adapters' SQL); the scope is not checked here: the preparation decides it and reads the pool again under its locks.
/// </summary>
public sealed record RetrieveAnswerCandidates(string Question, AiScope Scope, SemanticQuery? Vector, AnswerEffort Effort)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scope = ").Append(Scope).Append(", Hybrid = ").Append(Vector is not null).Append(", Effort = ").Append(Effort.Name);
        return true;
    }
}

public sealed record AnswerCandidates(IReadOnlyList<AuthorizedSource> Sources);

public sealed class RetrieveAnswerCandidatesHandler(IContentAccess access, SourceRegistry registry)
{
    public async Task<AnswerCandidates> Handle(RetrieveAnswerCandidates query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.Scope.Kind == AiScopeKind.Space ? query.Scope.SpaceId : null, ct);
        var scope = query.Scope switch
        {
            { Kind: AiScopeKind.Space, SpaceId: { } space } => AnswerScope.ForSpace(space),
            { Kind: AiScopeKind.Page, PageId: { } page } => AnswerScope.ForPage(page, query.Scope.SpaceId),
            _ => AnswerScope.Wiki,
        };
        return new AnswerCandidates((await registry.RetrieveAsync(AnswerRetrievalRequest.Pool(actor, query.Question, scope, query.Vector, query.Effort), ct)).Sources);
    }
}
