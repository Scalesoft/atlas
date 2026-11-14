using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Search;

namespace Atlas.Server.Features.AiSuggestions.Queries;

public sealed class GetCreationScopeHandler(CreationScopeReader reader, SemanticSuggestionSql sql)
{
    public async Task<CreationScope> Handle(GetCreationScope query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        return (await reader.ReadAsync(query.SpaceId, true, ct)).Scope;
    }
}

/// <summary>P2: the most common parent of the pages nearest to the new title, where the actor may create and every page of
/// the path is readable (SemanticSuggestionQueries.Placement).</summary>
public sealed class SuggestPlacementHandler(CreationScopeReader reader, SemanticSuggestionSql sql, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    private static readonly PlacementLookup Nothing = new(null);

    public async Task<PlacementLookup> Handle(SuggestPlacement query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        var (actor, scope) = await reader.ReadAsync(query.SpaceId, false, ct);
        if (!scope.MayCreate)
        {
            return Nothing;
        }
        try
        {
            if (!(await policy.ResolveAsync(actor.Id, ct)).Enabled) return Nothing;
            var row = (await sql.PlacementAsync(query.Semantic.GenerationId, query.Semantic.Vector, query.SpaceId, actor, scope.RootAllowed, ct)).SingleOrDefault();
            if (row is null)
            {
                return Nothing;
            }
            var ids = SemanticSuggestionSql.JsonArray<Guid>((string?)row[1]);
            var titles = SemanticSuggestionSql.JsonArray<string>((string?)row[2]);
            var path = ids.Zip(titles, (id, title) => new PlacementPathItem(id, title)).ToArray();
            var parentId = (Guid?)row[0];
            return new PlacementLookup(new PlacementSuggestion(parentId, parentId is null ? null : path[^1].Title, path));
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return Nothing;
        }
    }
}

/// <summary>P3: a template the actor may use in the space, by the dictionary (always) or by meaning (with a vector).</summary>
public sealed class SuggestTemplateHandler(CreationScopeReader reader, SemanticSuggestionSql sql, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    private static readonly TemplateLookup Nothing = new(null);

    public async Task<TemplateLookup> Handle(SuggestTemplate query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        var (actor, scope) = await reader.ReadAsync(query.SpaceId, false, ct);
        if (!scope.MayCreate)
        {
            return Nothing;
        }
        try
        {
            var semantic = query.Semantic is not null && (await policy.ResolveAsync(actor.Id, ct)).Enabled ? query.Semantic : null;
            var row = (await sql.TemplateAsync(semantic, query.Title, query.SpaceId, actor, ct)).SingleOrDefault();
            return row is null ? Nothing : new TemplateLookup(new TemplateSuggestion((Guid)row[0], (string)row[1], (string)row[2]));
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return Nothing;
        }
    }
}
