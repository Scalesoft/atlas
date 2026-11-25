using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Search.Queries;

public sealed class SearchHandler
{
    private readonly IContentAccess m_access;
    private readonly SearchSql m_sql;
    private readonly Atlas.Server.Features.Ai.Access.AiPolicyResolver m_policy;
    private readonly ISearchMissRecorder m_misses;
    private readonly ContentParticipants m_participants;
    private readonly PageVerifications.PageVerificationReader m_verifications;

    public SearchHandler(IContentAccess access, SearchSql sql, ISearchMissRecorder misses, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy,
        ContentParticipants participants, PageVerifications.PageVerificationReader verifications)
    {
        m_access = access;
        m_verifications = verifications;
        m_sql = sql;
        m_policy = policy;
        m_misses = misses;
        m_participants = participants;
    }

    public async Task<SearchResponse> Handle(Search query, CancellationToken ct)
    {
        // The whole body runs under the 57014 guard: legacy set the GUCs, took the advisory lock and locked the
        // actor row inside its try, so a lock wait past statement_timeout answers 503 rather than a driver error.
        try
        {
            // S1/S2: a query vector made by the endpoint (never here: this handler runs in a transaction and holds the actor
            // lock). Drafts and the prefix pass stay lexical; ISemanticQuery gives them no vector in the first place.
            var hybrid = query.Semantic is not null && query.Fuzzy && query.Options.Content is "all" or "published";
            await m_sql.PrepareAsync(ct, hybrid);
            // Keep account revocation/reset serialized until results have been assembled.
            // Space ACL is checked in each retrieval statement's current MVCC snapshot.
            // The actor comes first because legacy resolved it in the endpoint argument: a revoked credential
            // answers 401 before an unsupported filter answers 400 or a too-short query returns an empty page.
            var actor = await m_access.LockActorAsync(null, ct);
            var changedSince = ValidateOptions(query.Options);
            var normalized = NormalizeQuery(query.Query, query.Offset, query.Limit);
            if (!SearchText.IsSearchable(normalized))
            {
                return new SearchResponse([], false);
            }
            var parameters = SearchQueryParameters.Create(query.Query, normalized, actor.Id, actor.SecurityStamp, query.SpaceId);
            if (!query.Fuzzy) parameters = parameters with { Fuzzy = false };
            // A generation retired since the query was embedded leaves the vector part empty in SQL; the response then must
            // not claim it ran, and today's lexical functions answer instead.
            hybrid = hybrid && (await m_policy.ResolveAsync(actor.Id, ct)).Enabled && await m_sql.GenerationActiveAsync(query.Semantic!.GenerationId, ct);
            // A next page of a relaxed result (the caller says so) is relaxed again: the strict query found nothing on its first page.
            var strict = query.Relaxed == true ? [] : await RetrieveRowsAsync(query, parameters, changedSince, hybrid, ct);
            var relaxed = strict.Count == 0 && query.Relaxed != false ? await RetrieveRelaxedAsync(query, parameters, changedSince, hybrid, ct) : null;
            var response = await BuildResponseAsync(query, actor, parameters, normalized, relaxed is { Count: > 0 } ? relaxed : strict, strict.Count, hybrid, ct);
            return relaxed is { Count: > 0 } ? response with { Relaxed = true } : response;
        }
        catch (Exception exception) when (SearchSql.IsStatementTimeout(exception) && !ct.IsCancellationRequested)
        {
            throw new ApiException(503, "Hledání trvalo příliš dlouho. Zkuste přesnější dotaz.");
        }
    }

    /// <summary>
    /// K9: a whole question rarely has all its words in one page, so when the strict query matched nothing (on a later page: nothing
    /// on its first page either, unless the caller already knows), its significant words are OR'ed (<see cref="SearchText.AnyWordExpression"/>).
    /// P2-7: only the full-text tier runs again (the title, prefix and vector tiers of the same query found nothing already), under its own
    /// short statement timeout, and one it exceeds is no result. Null when the query has nothing to relax.
    /// </summary>
    private async Task<IList<object[]>?> RetrieveRelaxedAsync(Search query, SearchQueryParameters parameters, DateTime? changedSince, bool hybrid, CancellationToken ct)
    {
        if (SearchText.AnyWordExpression(query.Query) is not { } words
            || (query.Offset > 0 && query.Relaxed is null && (await RetrieveRowsAsync(query with { Offset = 0, Limit = 1 }, parameters, changedSince, hybrid, ct)).Count > 0))
        {
            return null;
        }
        return await m_sql.BoundedAsync(() => RetrieveRowsAsync(query, parameters with { Fts = words, Fuzzy = false }, changedSince, false, ct),
            RelaxedTimeoutMilliseconds, [], ct);
    }

    /// <summary>The fallback's own statement timeout: a quarter of the search's.</summary>
    private const int RelaxedTimeoutMilliseconds = 500;

    private Task<IList<object[]>> RetrieveRowsAsync(Search query, SearchQueryParameters parameters, DateTime? changedSince, bool hybrid, CancellationToken ct)
    {
        var take = query.Limit + 1;
        var latest = query.Options.Sort == "published";
        var unfiltered = query.Options is { ChangedWithin: "any", Sort: "relevance" };
        return query.Options.Content switch
        {
            "drafts" => m_sql.DraftsAsync(parameters, take, query.Offset, changedSince, latest, ct),
            "published" when hybrid => m_sql.HybridPublishedAsync(parameters, take, query.Offset, changedSince, latest, query.Semantic!, ct),
            "published" when unfiltered => m_sql.PublishedAsync(parameters, take, query.Offset, ct),
            "published" => m_sql.PublishedFilteredAsync(parameters, take, query.Offset, changedSince, latest, ct),
            _ when hybrid => m_sql.HybridAllAsync(parameters, take, query.Offset, changedSince, latest, query.Semantic!, ct),
            _ => m_sql.AllAsync(parameters, take, query.Offset, changedSince, latest, ct),
        };
    }

    private async Task<(string? DidYouMean, IReadOnlyList<SearchSpaceHit> Spaces)> ReadExtrasAsync(Search query, SearchQueryParameters parameters, int resultCount, CancellationToken ct)
    {
        string? didYouMean = null;
        // The title suggestion is not part of the MCP AI-scope filter.
        if (query.Offset == 0 && query.Fuzzy && query.Options.Content != "drafts" && !query.Options.AiScopeOnly
            && resultCount < Math.Min(3, query.Limit + 1))
            didYouMean = await m_sql.DidYouMeanAsync(parameters, ct);
        IReadOnlyList<SearchSpaceHit> spaces = [];
        if (query.Offset == 0 && query.Options == new SearchOptions())
            spaces = (await m_sql.SpacesAsync(parameters, ct)).Select(MapSpace).ToArray();
        return (didYouMean, spaces);
    }

    /// <param name="strictCount">The rows the query itself found: a relaxed result is still a miss of the query and leaves room for
    /// "did you mean" (S2-3).</param>
    private async Task<SearchResponse> BuildResponseAsync(Search query, UserAccount actor, SearchQueryParameters parameters, string normalized, IList<object[]> rows,
        int strictCount, bool hybrid, CancellationToken ct)
    {
        var items = rows.Select(row => MapHit(row, normalized, query.Options.Content == "drafts")).ToList();
        // Review U3-2: a fragment that cannot show the hit beside a requirement summary is cut again from the page's text.
        var recut = items.Select((item, i) => (item, i)).Where(x => !x.item.Semantic && SearchText.NeedsPageText((string)rows[x.i][4], normalized)).ToArray();
        if (recut.Length > 0)
        {
            var texts = await m_sql.PageTextsAsync(recut.Select(x => (x.item.Id, x.item.IsDraft)).ToArray(), ct);
            foreach (var (item, i) in recut)
            {
                if (texts.TryGetValue((item.Id, item.IsDraft), out var text))
                {
                    items[i] = item with { Snippet = SearchText.Snippet(text, normalized) };
                }
            }
        }
        var (didYouMean, spaces) = await ReadExtrasAsync(query, parameters, strictCount, ct);
        if (query.RecordMiss && query.Offset == 0 && query.Options is { Content: "all" or "published", ChangedWithin: "any" })
        {
            m_misses.Observe(new SearchObservation(actor.Id, query.SpaceId ?? query.ContextSpaceId, query.Query, normalized,
                strictCount > 0 || spaces.Count > 0));
        }
        // No cap on paging (infinite results in the search, 2026-10-28): the window ends where the results do.
        var hasMore = items.Count > query.Limit;
        if (items.Count > query.Limit) items.RemoveAt(query.Limit);
        if (query.Options.AiScopeOnly && items.Count > 0)
        {
            // Filter after the window: paging continues to describe the original lexical/hybrid order.
            var excluded = await m_sql.ExcludedFromAiAsync(items.Select(item => item.SpaceId).ToHashSet(), ct);
            items.RemoveAll(item => excluded.Contains(item.SpaceId));
        }
        if (items.Count > 0 && await m_verifications.StatesAsync(items.Select(item => item.Id).ToList(), ct) is { Count: > 0 } states)
        {
            for (var i = 0; i < items.Count; i++)
            {
                items[i] = items[i] with { VerificationState = states.TryGetValue(items[i].Id, out var state) ? state : null };
            }
        }
        var resources = new List<SearchResourceHit>();
        if (query.Offset == 0 && query.Options is { Content: "all" or "published", ChangedWithin: "any", AiScopeOnly: false })
        {
            foreach (var participant in m_participants.Search)
            {
                resources.AddRange((await participant.SearchAsync(actor, query.Query!, normalized, query.SpaceId, query.Limit, ct))
                    .Select(hit => new SearchResourceHit(hit.Type, hit.Id, hit.SpaceId, hit.SpaceName, hit.Title, hit.Snippet) { Target = hit.Target }));
            }
        }
        return new SearchResponse(items, hasMore)
        {
            Spaces = spaces, Resources = resources.Count > 0 ? resources : null, NextOffset = hasMore ? query.Offset + query.Limit : null, Semantic = hybrid, DidYouMean = didYouMean,
        };
    }

    private static string NormalizeQuery(string? query, int offset, int limit)
    {
        if (!SearchPaging.Valid(offset, limit))
        {
            throw new ApiException(400, SearchPaging.Message);
        }
        try
        {
            return SearchText.Normalize(query);
        }
        catch (ArgumentException exception)
        {
            throw new ApiException(400, exception.Message);
        }
    }

    private static DateTime? ValidateOptions(SearchOptions options)
    {
        if (options.Content is not ("all" or "published" or "drafts") || options.Sort is not ("relevance" or "published"))
        {
            throw new ApiException(400, "Vyberte podporovaný rozsah obsahu a řazení.");
        }
        return options.ChangedWithin switch
        {
            "any" => null,
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-30),
            _ => throw new ApiException(400, "Vyberte podporované období poslední změny.")
        };
    }

    private static SearchHit MapHit(object[] row, string normalized, bool drafts)
    {
        var changedAt = (DateTime?)row[5];
        // The unified scope mixes both kinds, so it reports draftness per row; the single-scope calls return
        // nine columns and the request already says which kind every row is. A hybrid row has four more
        // (semantic, passage and its anchor) after is_draft.
        var isDraft = row.Length > 10 ? (bool)row[9] : drafts;
        var hybrid = row.Length > 11;
        var semantic = hybrid && (bool)row[10];
        var passage = hybrid && row[11] is string text ? new SearchPassage(SearchText.Passage(text), row[12] as string, row[13] as string) : null;
        // A meaning-only page has no query word to centre a snippet on: it shows its passage (the start of its body when the
        // passage is only a title).
        var snippet = semantic && passage is { Text.Length: > 0 } ? passage.Text : SearchText.Snippet((string)row[4], normalized);
        return new SearchHit((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3], snippet)
        {
            IsDraft = isDraft,
            Widgets = SearchWidgets.Read((string)row[^1], normalized),
            ChangedAt = changedAt,
            PublishedAt = isDraft ? null : changedAt,
            HeadingTitle = row[6] as string,
            HeadingId = row[7] as string,
            MatchedAlias = row[8] as string,
            Semantic = semantic,
            Passage = passage,
            PassageBody = passage is null ? null : (string)row[11],
        };
    }

    private static SearchSpaceHit MapSpace(object[] row) => new((Guid)row[0], (string)row[1], (string)row[2], (string)row[3]);
}
