using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Search;

namespace Atlas.Server.Features.AiSuggestions.Queries;

/// <summary>E2, rule-based part: exact normalized title and alias matches of significant phrases (<see cref="LinkPhrasePolicy"/>),
/// published pages the actor may read only, never the page being written.</summary>
public sealed class FindLinkCandidatesHandler(AssistPageGuard guard, SemanticSuggestionSql sql)
{
    public async Task<LinkCandidateLookup> Handle(FindLinkCandidates query, CancellationToken ct)
    {
        await sql.PrepareAsync(false, ct);
        var (actor, page) = await guard.RequireAsync(query.PageId, ct);
        var phrases = query.Phrases.Select(LinkPhrasePolicy.Classify).OfType<LinkPhrase>().ToArray();
        if (phrases.Length == 0)
        {
            return new LinkCandidateLookup([], [], false);
        }
        IList<object[]> rows;
        try
        {
            rows = await sql.LinkCandidatesAsync(phrases.Select(x => x.Normalized).Distinct(StringComparer.Ordinal).ToArray(), actor, page.Id, page.SpaceId, ct);
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return new LinkCandidateLookup([], [], false);
        }
        var byNormalized = rows.ToLookup(row => (string)row[0], StringComparer.Ordinal);
        var items = new List<LinkCandidate>();
        var meaning = new List<string>();
        foreach (var phrase in phrases.DistinctBy(x => x.Phrase, StringComparer.Ordinal))
        {
            var matches = byNormalized[phrase.Normalized].ToArray();
            items.AddRange(matches.Select(row => new LinkCandidate(phrase.Phrase, (Guid)row[1], (Guid)row[2], (string)row[3], (string)row[4], (string)row[5])));
            if (matches.Length == 0 && phrase.Meaning)
            {
                meaning.Add(phrase.Phrase);
            }
        }
        return new LinkCandidateLookup(items, meaning, meaning.Count > 0 && await sql.SpaceInAiAsync(page.SpaceId, ct));
    }
}

/// <summary>E2, vector part: the nearest readable published title of each embedded phrase, at 0.80 or more (O17).</summary>
public sealed class FindMeaningLinkCandidatesHandler(AssistPageGuard guard, SemanticSuggestionSql sql, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    public async Task<LinkCandidatesResponse> Handle(FindMeaningLinkCandidates query, CancellationToken ct)
    {
        await sql.PrepareAsync(true, ct);
        var (actor, page) = await guard.RequireAsync(query.PageId, ct);
        if (query.Phrases.Count == 0 || query.Phrases.Count != query.Vectors.Count || !(await policy.ResolveAsync(actor.Id, ct)).Enabled)
        {
            return new LinkCandidatesResponse([]);
        }
        try
        {
            var rows = await sql.LinkCandidatesSemanticAsync(query.GenerationId, query.Phrases, query.Vectors, actor, page.Id, ct);
            return new LinkCandidatesResponse(rows.Select(row => new LinkCandidate((string)row[0], (Guid)row[1], (Guid)row[2], (string)row[3],
                (string)row[4], "meaning")).ToArray());
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            return new LinkCandidatesResponse([]);
        }
    }
}
