using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Platform.Validation;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// The pages of page cards, through their ContentResource identity and the page decision of IContentAccess: one query for the
/// resources, one for the pages, one grant query per space, one for the titles and excerpts and one for the space names, however
/// many cards a board has.
/// </summary>
public sealed class BoardPageTargets
{
    /// <summary>The excerpt of a page card, in characters.</summary>
    public const int ExcerptLength = 300;

    // Markdown markers, tags and links take room, so more than the excerpt is read.
    private const int ExcerptSource = 4 * ExcerptLength;

    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public BoardPageTargets(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    /// <summary>Before the locks: the space of each id that is a page (an atlas.page resource); other ids are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> LocateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }
        var rows = await m_session.QueryOver<ContentResource>()
            .WhereRestrictionOn(x => x.Id).IsIn(ids.Distinct().ToArray())
            .And(x => x.TypeId == PagesAtlasModule.ContentType)
            .Select(x => x.Id, x => x.SpaceId)
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (Guid)row[1]);
    }

    /// <summary>The pages among <paramref name="ids"/> that sit in the trash and that the account could read: their card says so
    /// (Trashed) instead of looking like a page it may not open, and CanRestore says whether it may restore it (the space's Admin, as
    /// UntrashPage). A trashed page the account could not read stays absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, BoardTargetDto>> TrashedAsync(OpenedBoard board, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var located = ids.Where(board.PageSpaces.ContainsKey).Distinct().ToArray();
        if (located.Length == 0)
        {
            return new Dictionary<Guid, BoardTargetDto>();
        }
        var pages = (await m_session.QueryOver<Page>().WhereRestrictionOn(x => x.Id).IsIn(located).And(x => x.IsTrashed).ListAsync(ct))
            .Where(page => board.LockedSpaces.Contains(page.SpaceId))
            .ToArray();
        var decisions = await m_access.AuthorizePagesAsync(pages, AccessOperation.ReadContent, ct);
        var administers = new Dictionary<Guid, bool>();
        var result = new Dictionary<Guid, BoardTargetDto>();
        for (var index = 0; index < pages.Length; index++)
        {
            if (!decisions[index].Allowed)
            {
                continue;
            }
            var page = pages[index];
            if (!administers.TryGetValue(page.SpaceId, out var admin))
            {
                try
                {
                    await m_access.RequireSpaceAsync(page.SpaceId, SpaceRole.Admin, ct);
                    admin = true;
                }
                catch (ApiException)
                {
                    admin = false;
                }
                administers[page.SpaceId] = admin;
            }
            result[page.Id] = BoardTargetDto.Unavailable(page.Id) with { Trashed = true, SpaceId = page.SpaceId, CanRestore = admin ? true : null };
        }
        return result;
    }

    /// <summary>
    /// Under the locks of <paramref name="board"/>: the pages among <paramref name="ids"/> the account may open, as their cards show
    /// them: the title and an excerpt of the revision it would see (the working one when it may edit, else the published one, as
    /// PageReader), the space's name and, for its editors, CanEdit. Missing, trashed, moved, unreadable and never
    /// published-for-this-reader pages are all simply absent (a trashed readable one: TrashedAsync, for the card only; it is never placeable).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BoardTargetDto>> ReadableAsync(OpenedBoard board, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var located = ids.Where(board.PageSpaces.ContainsKey).Distinct().ToArray();
        if (located.Length == 0)
        {
            return new Dictionary<Guid, BoardTargetDto>();
        }
        // A page moved since LocateAsync sits in a space this transaction did not lock; its card reads as unavailable this time.
        var pages = (await m_session.QueryOver<Page>().WhereRestrictionOn(x => x.Id).IsIn(located).ListAsync(ct))
            .Where(page => !page.IsTrashed && board.LockedSpaces.Contains(page.SpaceId))
            .ToArray();
        if (pages.Length == 0)
        {
            return new Dictionary<Guid, BoardTargetDto>();
        }
        var decisions = await m_access.AuthorizePagesAsync(pages, AccessOperation.ReadContent, ct);
        var working = new List<Guid>();
        var published = new List<Guid>();
        for (var index = 0; index < pages.Length; index++)
        {
            if (!decisions[index].Allowed)
            {
                continue;
            }
            if (decisions[index].CanEdit)
            {
                working.Add(pages[index].Id);
            }
            else if (pages[index].PublishedRevisionId is { } revision)
            {
                published.Add(revision);
            }
        }
        if (working.Count == 0 && published.Count == 0)
        {
            return new Dictionary<Guid, BoardTargetDto>();
        }
        // Only the start of the Markdown is read: SearchText.Excerpt takes its readable text, password references and
        // a directive the cut halves included.
        var revisions = await m_session.QueryOver<PageRevision>()
            .Where(Restrictions.Or(
                Restrictions.And(Restrictions.In(Projections.Property<PageRevision>(x => x.PageId), working.ToArray()), Restrictions.IsNull(Projections.Property<PageRevision>(x => x.ActiveTo))),
                Restrictions.In(Projections.Property<PageRevision>(x => x.Id), published.ToArray())))
            .Select(Projections.Property<PageRevision>(x => x.PageId), Projections.Property<PageRevision>(x => x.Title),
                Projections.SqlFunction("substring", NHibernateUtil.String, Projections.Property<PageRevision>(x => x.Markdown), Projections.Constant(1), Projections.Constant(ExcerptSource)))
            .ListAsync<object[]>(ct);
        var spaceIds = pages.Select(page => page.SpaceId).Distinct().ToArray();
        var spaceNames = (await m_session.QueryOver<Space>().WhereRestrictionOn(x => x.Id).IsIn(spaceIds).Select(x => x.Id, x => x.Name).ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => (string)row[1]);
        var spaceOf = pages.ToDictionary(page => page.Id, page => page.SpaceId);
        var editable = working.ToHashSet();
        return revisions.ToDictionary(row => (Guid)row[0], row => new BoardTargetDto(PagesAtlasModule.ContentType, (Guid)row[0], (string)row[1],
            spaceNames.GetValueOrDefault(spaceOf[(Guid)row[0]]), SearchText.Excerpt((string?)row[2] ?? "", ExcerptLength) is { Length: > 0 } excerpt ? excerpt : null, true)
            {
                // Who may edit the page may open it for editing from its card (COL2-9); left out for a reader.
                CanEdit = editable.Contains((Guid)row[0]) ? true : null
            });
    }
}
