using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.AiSuggestions.Queries;

/// <summary>E4, first read: authorization and the text that may be embedded. Password references are removed here, on the
/// server, whatever the client already left out.</summary>
public sealed class PreparePageOrderHandler(AssistPageGuard guard, SemanticSuggestionSql sql)
{
    public async Task<PageOrderContext> Handle(PreparePageOrder query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        var (_, page) = await guard.RequireAsync(query.PageId, ct);
        var text = SearchText.StripSecretDirectives(query.Context).Trim();
        return new PageOrderContext(text, text.Length > 0 && query.Candidates.Count > 0 && await sql.SpaceInAiAsync(page.SpaceId, ct));
    }
}

/// <summary>E4, vector part: similarity of the readable candidates to the section, most similar first.</summary>
public sealed class RankPagesByContextHandler(AssistPageGuard guard, SemanticSuggestionSql sql, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    public async Task<PageOrderResponse> Handle(RankPagesByContext query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        var (actor, _) = await guard.RequireAsync(query.PageId, ct);
        if (query.Candidates.Count == 0 || !(await policy.ResolveAsync(actor.Id, ct)).Enabled)
        {
            return new PageOrderResponse([]);
        }
        try
        {
            var rows = await sql.RankByContextAsync(query.Semantic.GenerationId, query.Semantic.Vector, query.Candidates.Distinct().ToArray(), actor, ct);
            return new PageOrderResponse(rows.Select(row => new PageOrderItem((Guid)row[0], (float)row[1])).ToArray());
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return new PageOrderResponse([]);
        }
    }
}
