using NHibernate;
using NHibernate.Criterion;
using NHibernate.SqlCommand;
using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary.Queries;

/// <summary>
/// Current metadata only. Contribution is an indexed publication-relative projection,
/// so personal lists never fetch document bodies or scan revision history.
/// </summary>
public sealed class ListPersonalLibraryHandler
{
    private const int MaximumOffset = 1000;
    private readonly ISession m_session;
    private readonly PersonalPageGuard m_guard;
    private readonly ContentParticipants m_participants;

    public ListPersonalLibraryHandler(ISession session, PersonalPageGuard guard, ContentParticipants participants)
    {
        m_session = session;
        m_guard = guard;
        m_participants = participants;
    }

    public async Task<PersonalLibraryList> Handle(ListPersonalLibrary query, CancellationToken ct)
    {
        var kind = query.Kind;
        if (kind is not ("favorites" or "recent" or "drafts" or "updates" or "changes"))
        {
            throw new ApiException(400, "Neznámý seznam osobní knihovny.");
        }
        if (query.Offset is < 0 or > MaximumOffset || query.Limit is < 1 or > 50)
        {
            throw new ApiException(400, "Počet položek musí být 1–50 a posun 0–1000.");
        }
        var actor = await m_guard.LockIdentityAsync(null, false, ct);
        // Favorites and recent visits hold other content types too (boards): each source gives its first offset + limit + 1 rows
        // and the merge pages them; every other list is pages only.
        var others = kind is "favorites" or "recent" ? m_participants.Library : [];
        var window = query.Offset + query.Limit + 1;
        var actorId = actor.Id;
        var admin = await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct);

        LibraryPageEntry entry = null!;
        PageAccessEntry membership = null!;
        PageAccessEntry parentAccess = null!;
        PageFavorite favorite = null!;
        PageVisit visit = null!;
        PageContribution contribution = null!;

        var rows = m_session.QueryOver(() => entry)
            // Page roles from EffectivePageAccess (WP3 S3): every page, and its parent, is decided on its own.
            .JoinEntityAlias(() => membership, () => membership.PageId == entry.Id && membership.UserId == actorId)
            .JoinEntityAlias(() => parentAccess, () => parentAccess.PageId == entry.ParentId && parentAccess.UserId == actorId, JoinType.LeftOuterJoin)
            .JoinEntityAlias(() => favorite, () => favorite.PageId == entry.Id && favorite.UserId == actorId && favorite.ActiveTo == null, JoinType.LeftOuterJoin)
            .JoinEntityAlias(() => visit, () => visit.PageId == entry.Id && visit.UserId == actorId, JoinType.LeftOuterJoin)
            .JoinEntityAlias(() => contribution, () => contribution.PageId == entry.Id && contribution.UserId == actorId, JoinType.LeftOuterJoin)
            .Where(() => !entry.IsTrashed && !entry.IsTemplate);

        var role = Projections.Property(() => membership.Role);
        var published = Restrictions.Eq(Projections.Property(() => entry.IsPublished), true);
        var workingVisible = Restrictions.Ge(role, 2);
        var unpublishedWork = Restrictions.Or(
            Restrictions.IsNull(Projections.Property(() => entry.PublishedRevisionId)),
            Restrictions.NotEqProperty(Projections.Property(() => entry.WorkingRevisionId), Projections.Property(() => entry.PublishedRevisionId)));
        var ownConcept = admin
            ? workingVisible
            : Restrictions.And(workingVisible, Restrictions.Eq(Projections.Property(() => entry.OwnerId), actorId));
        rows = rows.Where(Restrictions.Or(published, ownConcept));

        switch (kind)
        {
            case "favorites":
                rows = rows.Where(Restrictions.IsNotNull(Projections.Property(() => favorite.Id)));
                break;
            case "recent":
                rows = rows.Where(Restrictions.IsNotNull(Projections.Property(() => visit.Id)));
                break;
            case "updates":
                rows = rows.Where(published);
                break;
            case "changes":
                break;
            default:
                var participated = Restrictions.GtProperty(
                    Projections.Property(() => contribution.LastRevisionVersion),
                    Projections.SqlFunction("coalesce", NHibernateUtil.Int32, Projections.Property(() => entry.PublishedVersion), Projections.Constant(0)));
                var mine = Restrictions.Eq(Projections.Property(() => entry.OwnerId), actorId);
                rows = rows.Where(Restrictions.And(workingVisible, Restrictions.And(unpublishedWork, Restrictions.Or(mine, participated))));
                break;
        }

        // role >= Writer sees the working title, everyone else (and every "updates" row) the published one.
        var title = kind == "updates"
            ? Projections.Property(() => entry.PublishedTitle)
            : Projections.Conditional(workingVisible, Projections.Property(() => entry.WorkingTitle), Projections.Property(() => entry.PublishedTitle));
        var changedAt = Projections.Conditional(
            Restrictions.And(workingVisible, Restrictions.Or(
                Restrictions.IsNull(Projections.Property(() => entry.PublishedAt)),
                Restrictions.GtProperty(Projections.Property(() => entry.WorkingActiveFrom), Projections.Property(() => entry.PublishedAt)))),
            Projections.Property(() => entry.WorkingActiveFrom),
            Projections.Property(() => entry.PublishedAt));
        var hasUnpublishedChanges = Projections.Conditional(
            Restrictions.And(workingVisible, unpublishedWork),
            Projections.Constant(true),
            Projections.Constant(false));
        var version = kind == "changes"
            ? Projections.Conditional(workingVisible, Projections.Property(() => entry.Version), Projections.Property(() => entry.PublishedVersion))
            : Projections.Property(() => entry.Version);
        IProjection order = kind switch
        {
            "favorites" => Projections.Property(() => favorite.ActiveFrom),
            "recent" => Projections.Property(() => visit.LastVisitedAt),
            "updates" => Projections.Property(() => entry.PublishedAt),
            "changes" => changedAt,
            _ => Projections.Property(() => entry.WorkingActiveFrom)
        };

        var page = await rows
            .Select(Projections.Property(() => entry.Id),
                Projections.Property(() => entry.SpaceId),
                Projections.Property(() => entry.SpaceName),
                title,
                Projections.Property(() => entry.ParentId),
                Projections.Property(() => entry.ParentIsTrashed),
                Projections.Property(() => entry.ParentIsTemplate),
                Projections.Property(() => entry.ParentIsPublished),
                Projections.Property(() => entry.ParentOwnerId),
                version,
                Projections.Property(() => entry.IsPublished),
                Projections.Property(() => favorite.Id),
                Projections.Property(() => visit.LastVisitedAt),
                Projections.Property(() => entry.PublishedAt),
                role,
                changedAt,
                hasUnpublishedChanges,
                Projections.Property(() => parentAccess.Role),
                Projections.Property(() => entry.PublishedChangeSummary),
                Projections.Property(() => entry.PublicationChannel),
                Projections.Property(() => entry.PublicationApiTokenId),
                order)
            .OrderBy(order).Desc
            .ThenBy(title).Asc
            .ThenBy(Projections.Property(() => entry.Id)).Asc
            .Skip(others.Count == 0 ? query.Offset : 0)
            .Take(others.Count == 0 ? query.Limit + 1 : window)
            .ListAsync<object[]>(ct);

        if (others.Count > 0)
        {
            var pageItems = await WithParentTitlesAsync(page.Select(row => Describe(row, actorId, admin, false, false)).ToArray(), page.ToArray(), ct);
            var merged = pageItems.Select((item, index) => (Item: item, At: (DateTime)page[index][21])).ToList();
            foreach (var participant in others)
            {
                merged.AddRange((await participant.ListAsync(kind, actor, window, ct)).Select(entry => (Item: new PersonalLibraryItem(entry.Id, entry.SpaceId,
                    entry.SpaceName, entry.Title, null, 0, true, entry.IsFavorite, entry.VisitedAt, null) { Type = entry.Type }, At: entry.OrderedAt)));
            }
            var ordered = merged.OrderByDescending(x => x.At).ThenBy(x => x.Item.Title, StringComparer.Ordinal).ThenBy(x => x.Item.Id.ToString("N"), StringComparer.Ordinal)
                .Skip(query.Offset).Select(x => x.Item).ToArray();
            var hasMore = ordered.Length > query.Limit && query.Offset + query.Limit <= MaximumOffset;
            return new PersonalLibraryList(ordered.Take(query.Limit).ToArray(), hasMore, hasMore ? query.Offset + query.Limit : null);
        }

        var items = page.Take(query.Limit).Select(row => Describe(row, actorId, admin, kind == "changes", kind == "updates")).ToArray();
        items = await WithParentTitlesAsync(items, page.Take(query.Limit).ToArray(), ct);
        if (kind == "updates")
        {
            items = await WithPublicationTokensAsync(items, page.Take(query.Limit).ToArray(), actor, ct);
        }
        var more = page.Count > query.Limit && query.Offset + query.Limit <= MaximumOffset;
        return new PersonalLibraryList(items, more, more ? query.Offset + query.Limit : null);
    }

    /// <summary>
    /// One more statement for at most a page of rows: the titles of the parents the account may list
    /// (Describe keeps ParentId only for those), working title for a writer of the parent, published otherwise.
    /// </summary>
    private async Task<PersonalLibraryItem[]> WithParentTitlesAsync(PersonalLibraryItem[] items, object[][] rows, CancellationToken ct)
    {
        var parentIds = items.Where(item => item.ParentId is not null).Select(item => item.ParentId!.Value).Distinct().ToArray();
        if (parentIds.Length == 0) return items;
        LibraryPageEntry parent = null!;
        var titles = (await m_session.QueryOver(() => parent)
                .WhereRestrictionOn(() => parent.Id).IsIn(parentIds)
                .Select(Projections.Property(() => parent.Id), Projections.Property(() => parent.WorkingTitle), Projections.Property(() => parent.PublishedTitle))
                .ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => (Working: (string)row[1], Published: row[2] as string));
        return items.Select((item, index) =>
        {
            if (item.ParentId is not { } parentId || !titles.TryGetValue(parentId, out var title)) return item;
            var writer = (rows[index][17] as int? ?? 0) >= 2;
            return item with { ParentTitle = writer ? title.Working : title.Published };
        }).ToArray();
    }

    /// <summary>Recent publications name the token only to its owner and wiki administrators; everyone else sees the channel.</summary>
    private async Task<PersonalLibraryItem[]> WithPublicationTokensAsync(PersonalLibraryItem[] items, object[][] rows, UserAccount actor, CancellationToken ct)
    {
        var tokens = await ApiTokenVisibility.VisibleAsync(m_session, actor, rows.Select(row => row[20] as Guid?), ct);
        return items.Select((item, index) => item with { PublicationApiToken = tokens.Find(rows[index][20] as Guid?) }).ToArray();
    }

    private static PersonalLibraryItem Describe(object[] row, Guid actorId, bool admin, bool changes, bool updates)
    {
        var parentId = (Guid?)row[4];
        // A parent the account cannot list is hidden: the item is shown without it, as in the tree (D8).
        var parentRole = row[17] as int? ?? 0;
        var parentVisible = parentId is not null && row[5] is false && row[6] is false && parentRole >= 1
            && (row[7] is true || parentRole >= 2 && ((Guid?)row[8] == actorId || admin));
        return new PersonalLibraryItem((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3],
            parentVisible ? parentId : null, (int)row[9], (bool)row[10], row[11] is not null, (DateTime?)row[12], (DateTime?)row[13])
        {
            ChangedAt = changes ? (DateTime?)row[15] : null,
            HasUnpublishedChanges = changes && (bool)row[16],
            ChangeSummary = updates ? row[18] as string : null,
            PublicationChannel = updates ? ContentChannels.ForRead(row[19] as string) : null
        };
    }
}
