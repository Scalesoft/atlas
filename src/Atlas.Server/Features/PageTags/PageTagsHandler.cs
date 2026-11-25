using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageTags.Domain;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Features.PageTags;

public sealed class PageTagsHandler(ISession session, PersonalPageGuard guard, OrganizationLocks locks, PageTagStore store, IRequestChannel channel)
{
    public async Task<PageTagState> Handle(GetPageTags query, CancellationToken ct)
    {
        await guard.RequirePageAsync(query.PageId, false, ct);
        return await store.ReadAsync(query.PageId, ct);
    }

    public async Task<PageTagState> Handle(SetPageTags command, CancellationToken ct)
    {
        // A readable page without EditContent answers 403, anything else 404.
        var (_, actor) = await locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        var names = TagNames.Validate(command.Request);
        return await ApplyAsync(command.PageId, actor, names, command.Request.ExpectedTagIds, ct);
    }

    /// <summary>
    /// Adds and removes the named tags and keeps every other one. The current set is read after the page lock of the set, so two
    /// concurrent changes of one page run one after the other and neither loses the other's tags; the optional expected ids are
    /// the same compare-and-set as the full set. A name to remove the page does not carry is ignored. The tags kept stay by id and
    /// only added names are resolved: a stored name today's rules refuse, or one that normalizes to another tag, never breaks or
    /// changes the tags the change does not name (D-L1).
    /// </summary>
    public async Task<PageTagState> Handle(ChangePageTags command, CancellationToken ct)
    {
        var (_, actor) = await locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        var request = command.Request;
        TagNames.ValidateChange(request);
        var removed = TagNames.Prepare(request.Remove ?? []).Select(x => x.NormalizedName).ToHashSet(StringComparer.Ordinal);
        var kept = (await store.ReadAsync(command.PageId, ct)).Tags.Where(tag => Normalized(tag.Name) is not { } name || !removed.Contains(name))
            .Select(x => x.Id).ToArray();
        return await ApplyAsync(command.PageId, actor, TagNames.Prepare(request.Add ?? []), request.ExpectedTagIds, ct, kept);
    }

    /// <summary>A stored name as a removal names it; null for a legacy name today's rules refuse (no removal can name it).</summary>
    private static string? Normalized(string name)
    {
        try
        {
            return TagNames.Normalize(name, 48, false).ToLowerInvariant();
        }
        catch (ApiException)
        {
            return null;
        }
    }

    private async Task<PageTagState> ApplyAsync(Guid pageId, UserAccount actor, (string Name, string NormalizedName)[] names,
        Guid[]? expectedTagIds, CancellationToken ct, Guid[]? kept = null)
    {
        var current = await session.QueryOver<PageTag>().Where(x => x.PageId == pageId && x.ActiveTo == null)
            .Take(20).ListAsync(ct);
        if (expectedTagIds is not null && !current.Select(x => x.TagId).ToHashSet().SetEquals(expectedTagIds))
            throw new ApiException(409, "Štítky byly změněny. Načtěte je znovu.", "tags_changed");

        var desired = new HashSet<Guid>(kept ?? []);
        foreach (var name in names)
            desired.Add(await store.ResolveAsync(name.Name, name.NormalizedName, actor, ct));
        if (desired.Count > 20) throw TagNames.Invalid();
        var added = desired.Except(current.Select(x => x.TagId)).ToArray();
        var boundaries = await store.PreviousBoundariesAsync(pageId, added, ct);
        var now = DateTime.UtcNow;
        foreach (var assignment in current.Where(x => !desired.Contains(x.TagId)))
            assignment.ActiveTo = Temporal.NextBoundary(assignment.ActiveFrom, now);
        foreach (var tagId in added)
        {
            var start = boundaries.TryGetValue(tagId, out var previous) ? Temporal.NextBoundary(previous, now) : now;
            await session.SaveAsync(new PageTag
            {
                Id = Guid.CreateVersion7(), PageId = pageId, TagId = tagId, CreatedById = actor.Id,
                CreatedAt = now, ActiveFrom = start
            }, ct);
        }
        await session.FlushAsync(ct);
        return await store.ReadAsync(pageId, ct);
    }

    public async Task<TagSuggestions> Handle(SuggestTags query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var normalized = TagNames.Normalize(query.Query, 200, true).ToLowerInvariant();
        return await store.SuggestAsync(actor, normalized, Ai(query.AiScopeOnly), ct);
    }

    public async Task<SearchResponse> Handle(ListTaggedPages query, CancellationToken ct)
    {
        var actor = await guard.LockIdentityAsync(null, false, ct);
        if (!SearchPaging.Valid(query.Offset, query.Limit))
            throw new ApiException(400, SearchPaging.Message);
        if (query.Options.Content is not ("all" or "published" or "drafts") || query.Options.Sort is not ("relevance" or "published"))
            throw new ApiException(400, "Vyberte podporovaný rozsah obsahu a řazení.");
        DateTime? since = query.Options.ChangedWithin switch
        {
            "any" => null,
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-30),
            _ => throw new ApiException(400, "Vyberte podporované období poslední změny.")
        };
        return await store.BrowseAsync(actor, query, TagNames.Normalize(query.Query, 200, true).ToLowerInvariant(), since, Ai(query.AiScopeOnly), ct);
    }

    /// <summary>G5: a channel whose content may go to a model sees nothing of a space switched out of AI (RequestChannel.IsAiBound).</summary>
    private bool Ai(bool requested) => requested || channel.Current.IsAiBound;
}
