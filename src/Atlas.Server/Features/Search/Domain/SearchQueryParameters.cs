namespace Atlas.Server.Features.Search.Domain;

/// <summary>The eight arguments every atlas_search_* function shares, derived once per request from the raw query.</summary>
public sealed record SearchQueryParameters(string Query, string Prefix, string Fts, bool Phrase, bool Fuzzy,
    Guid Actor, string Stamp, Guid? Space)
{
    public static SearchQueryParameters Create(string? rawQuery, string normalized, Guid actorId, string stamp, Guid? spaceId)
    {
        var expression = SearchText.QueryExpression(rawQuery);
        return new SearchQueryParameters(normalized, normalized + "%", expression.Expression, expression.HasPhrase,
            !expression.HasPhrase && normalized.Length >= 3, actorId, stamp, spaceId);
    }
}
