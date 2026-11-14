using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.AiSuggestions.Queries;
using Atlas.Server.Features.Search.Domain;
using Wolverine;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>
/// The endpoint side of E2, E4, P2 and P3 (plan §13): validation, the capability from memory, a short read, the model call
/// outside any transaction and lock (<see cref="WorkingTextEmbedder"/>, 300 ms budget), then a second short read that
/// authorizes again. Every endpoint returns its rule-based part whenever assist or creation allows it, and the vector part
/// only with the capability (which includes the admin's permission to send working text to a cloud profile); an endpoint
/// without a rule-based part (E4, P2) answers 503 <c>ai_unavailable</c> without it. A vector that did not arrive in time
/// leaves the answer without its vector part: an empty order (E4) or nothing to suggest (P2).
/// </summary>
public sealed class AiSuggestionsFlow(IMessageBus bus, WorkingTextEmbedder embedder, AiGate gate)
{
    /// <summary>E2. 400 for a malformed request, 404 without EditContent or assist.</summary>
    public async Task<LinkCandidatesResponse> LinkCandidatesAsync(LinkCandidatesRequest request, CancellationToken ct)
    {
        if (request.Phrases is not { } phrases || phrases.Count > LinkPhrasePolicy.MaxPhrases
            || phrases.Any(phrase => phrase is null || phrase.Length > LinkPhrasePolicy.MaxPhraseLength))
        {
            throw new ApiException(400, "Pošlete nejvýše {maxPhrases} frází, každou nejvýše o {maxPhraseLength} znacích.", args: new Dictionary<string, object?> { ["maxPhrases"] = LinkPhrasePolicy.MaxPhrases, ["maxPhraseLength"] = LinkPhrasePolicy.MaxPhraseLength });
        }
        var lookup = await bus.InvokeAsync<LinkCandidateLookup>(new FindLinkCandidates(request.PageId, phrases), ct);
        if (!lookup.InAi || lookup.MeaningPhrases.Count == 0)
        {
            return new LinkCandidatesResponse(lookup.Items);
        }
        var vectors = await embedder.EmbedAsync(AiTaskCatalog.EditorLink, lookup.MeaningPhrases, ct);
        if (vectors is not { Any: true })
        {
            return new LinkCandidatesResponse(lookup.Items);
        }
        var embedded = lookup.MeaningPhrases.Zip(vectors.Vectors).Where(x => x.Second is not null).ToArray();
        var meaning = await bus.InvokeAsync<LinkCandidatesResponse>(new FindMeaningLinkCandidates(request.PageId,
            embedded.Select(x => x.First).ToArray(), vectors.GenerationId, embedded.Select(x => x.Second!).ToArray()), ct);
        return new LinkCandidatesResponse([.. lookup.Items, .. meaning.Items]);
    }

    /// <summary>E4. 503 without the <c>editorLinks</c> capability, then 400 and 404 as E2.</summary>
    public async Task<PageOrderResponse> PageOrderAsync(PageOrderRequest request, CancellationToken ct)
    {
        await gate.RequireAsync(AiTaskCatalog.EditorLink, ct);
        if (request.Context is not { Length: <= PreparePageOrder.MaxContextLength } context
            || request.Candidates is not { Count: <= PreparePageOrder.MaxCandidates } candidates)
        {
            throw new ApiException(400, "Pošlete text sekce nejvýše o {maxContextLength} znacích a nejvýše {maxCandidates} stránek.", args: new Dictionary<string, object?> { ["maxContextLength"] = PreparePageOrder.MaxContextLength, ["maxCandidates"] = PreparePageOrder.MaxCandidates });
        }
        var prepared = await bus.InvokeAsync<PageOrderContext>(new PreparePageOrder(request.PageId, context, candidates), ct);
        if (!prepared.InAi)
        {
            return new PageOrderResponse([]);
        }
        var vectors = await embedder.EmbedAsync(AiTaskCatalog.EditorLink, [prepared.Text], ct);
        if (vectors?.Vectors[0] is not { } vector)
        {
            return new PageOrderResponse([]);
        }
        return await bus.InvokeAsync<PageOrderResponse>(new RankPagesByContext(request.PageId, candidates, new SemanticQuery(vectors.GenerationId, vector)), ct);
    }

    /// <summary>P2. 503 without the <c>createSuggestions</c> capability; null (204) where the actor may not create, the space is
    /// excluded from AI, the title is too short or nothing fits.</summary>
    public async Task<PlacementSuggestion?> PlacementAsync(string? title, Guid spaceId, CancellationToken ct)
    {
        await gate.RequireAsync(AiTaskCatalog.PageCreateSuggest, ct);
        if (!Embeddable(title))
        {
            return null;
        }
        var scope = await bus.InvokeAsync<CreationScope>(new GetCreationScope(spaceId), ct);
        if (!scope.MayCreate || !scope.InAi)
        {
            return null;
        }
        var vectors = await embedder.EmbedAsync(AiTaskCatalog.PageCreateSuggest, [title!.Trim()], ct);
        if (vectors?.Vectors[0] is not { } vector)
        {
            return null;
        }
        return (await bus.InvokeAsync<PlacementLookup>(new SuggestPlacement(spaceId, new SemanticQuery(vectors.GenerationId, vector)), ct)).Suggestion;
    }

    /// <summary>P3. The dictionary always; the vector only with the <c>createSuggestions</c> capability in a space included in
    /// AI. Null (204) when nothing fits or the actor may not create in the space.</summary>
    public async Task<TemplateSuggestion?> TemplateAsync(string? title, Guid spaceId, CancellationToken ct)
    {
        if (!Embeddable(title))
        {
            return null;
        }
        var normalized = SearchText.Normalize(title);
        SemanticQuery? semantic = null;
        if (await embedder.AvailableAsync(AiTaskCatalog.PageCreateSuggest, ct))
        {
            var scope = await bus.InvokeAsync<CreationScope>(new GetCreationScope(spaceId), ct);
            if (!scope.MayCreate)
            {
                return null;
            }
            if (scope.InAi && await embedder.EmbedAsync(AiTaskCatalog.PageCreateSuggest, [title!.Trim()], ct) is { } vectors && vectors.Vectors[0] is { } vector)
            {
                semantic = new SemanticQuery(vectors.GenerationId, vector);
            }
        }
        return (await bus.InvokeAsync<TemplateLookup>(new SuggestTemplate(spaceId, normalized) { Semantic = semantic }, ct)).Suggestion;
    }

    /// <summary>A title worth a suggestion: at most 200 characters (400 above, as search), at least 3 after normalization.</summary>
    private static bool Embeddable(string? title)
    {
        try
        {
            return SearchText.Normalize(title).Length >= 3;
        }
        catch (ArgumentException exception)
        {
            throw new ApiException(400, exception.Message);
        }
    }
}
