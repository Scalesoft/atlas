using Atlas.Server.Features.PageHistory.Contracts;
using Atlas.Server.Features.PageHistory.Domain;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Queries;

public sealed class GetPageHistoryHandler
{
    private readonly ISession m_session;
    private readonly PageHistoryAccess m_pages;

    public GetPageHistoryHandler(ISession session, PageHistoryAccess pages)
    {
        m_session = session;
        m_pages = pages;
    }

    /// <summary>The versions of the page newest first (3.6-C): each is its newest revision, whose stored changes cover the whole version.
    /// With <see cref="GetPageHistory.Group"/>, the revisions (steps) of that one version.</summary>
    public async Task<PageHistoryList> Handle(GetPageHistory query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 50 || query.BeforeVersion is <= 0)
        {
            throw new ApiException(400, "Neplatné stránkování historie.");
        }
        var (page, actor) = await m_pages.RequirePageAsync(query.Id, false, ct);
        // Metadata only, never rich bodies. A version is the revision no later revision of its group follows (IX_PageRevision_Group), unless
        // it ended where it started (SameAsRevisionId). The revision such a current or published one equals stands for it ("Current", "Published").
        var sql = m_session.CreateSQLQuery($"""
                SELECT r."Id",r."Version",r."Title",r."AuthorId",r."ActiveFrom",r."ActiveTo",r."Channel",r."ApiTokenId",r."GroupId",
                  r."GroupStartedAt",r."CheckpointCount",r."IsExplicit",r."Label",r."BaseRevisionId",r."AiAssisted",CAST(r."ChangeSummary" AS text) AS "Changes",
                  (SELECT coalesce(c."SameAsRevisionId",c."Id") FROM "PageRevision" c WHERE c."PageId"=:page AND c."ActiveTo" IS NULL) AS "Current",
                  (SELECT coalesce(s."SameAsRevisionId",s."Id") FROM "Page" p JOIN "PageRevision" s ON s."Id"=p."PublishedRevisionId" WHERE p."Id"=:page) AS "Published"
                FROM "PageRevision" r
                WHERE r."PageId"=:page{(query.BeforeVersion is null ? "" : " AND r.\"Version\"<:before")}
                  AND {(query.Group is null
                      ? "r.\"SameAsRevisionId\" IS NULL AND NOT EXISTS (SELECT 1 FROM \"PageRevision\" n WHERE n.\"PageId\"=r.\"PageId\" AND n.\"GroupId\"=r.\"GroupId\" AND n.\"Version\">r.\"Version\")"
                      : "r.\"GroupId\"=:group")}
                ORDER BY r."Version" DESC LIMIT :take
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Version", NHibernateUtil.Int32).AddScalar("Title", NHibernateUtil.String)
            .AddScalar("AuthorId", NHibernateUtil.Guid).AddScalar("ActiveFrom", NHibernateUtil.UtcDateTime).AddScalar("ActiveTo", NHibernateUtil.UtcDateTime)
            .AddScalar("Channel", NHibernateUtil.String).AddScalar("ApiTokenId", NHibernateUtil.Guid).AddScalar("GroupId", NHibernateUtil.Guid)
            .AddScalar("GroupStartedAt", NHibernateUtil.UtcDateTime).AddScalar("CheckpointCount", NHibernateUtil.Int32).AddScalar("IsExplicit", NHibernateUtil.Boolean)
            .AddScalar("Label", NHibernateUtil.String).AddScalar("BaseRevisionId", NHibernateUtil.Guid).AddScalar("AiAssisted", NHibernateUtil.Boolean)
            .AddScalar("Changes", NHibernateUtil.String).AddScalar("Current", NHibernateUtil.Guid).AddScalar("Published", NHibernateUtil.Guid)
            .SetParameter("page", query.Id).SetParameter("take", query.Limit + 1);
        if (query.BeforeVersion is { } before)
        {
            sql.SetParameter("before", before);
        }
        if (query.Group is { } group)
        {
            sql.SetParameter("group", group);
        }
        var rows = await sql.ListAsync<object[]>(ct);
        var visible = rows.Take(query.Limit).ToArray();
        if (visible.Length == 0)
        {
            return new PageHistoryList(query.Id, page.Version, [], null);
        }
        var changes = visible.Select(row => RevisionVersions.Read(row[15] as string)).ToArray();
        var people = visible.Select(row => (Guid)row[3]).Concat(changes.SelectMany(x => x?.Contributors ?? []).Select(x => x.Id)).Distinct().ToArray();
        var authors = (await m_session.QueryOver<UserAccount>().WhereRestrictionOn(x => x.Id).IsIn(people)
                .Select(x => x.Id, x => x.DisplayName).ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => (string)row[1]);
        var revisionIds = visible.Select(row => (Guid)row[0]).ToArray();
        // Metadata of the last publication of each revision. A revision has more than one only after a backup restore re-appended
        // its history, so the rows run in ActiveFrom order and the later one wins.
        var published = new Dictionary<Guid, LastPublication>();
        foreach (var row in await m_session.QueryOver<PagePublication>().Where(x => x.PageId == query.Id)
                     .WhereRestrictionOn(x => x.RevisionId).IsIn(revisionIds)
                     .OrderBy(x => x.ActiveFrom).Asc
                     .SelectList(list => list.Select(x => x.RevisionId).Select(x => x.ActiveFrom).Select(x => x.ChangeSummary)
                         .Select(x => x.Channel).Select(x => x.ApiTokenId))
                     .ListAsync<object[]>(ct))
        {
            published[(Guid)row[0]] = new LastPublication((DateTime)row[1], row[2] as string, (string)row[3], row[4] as Guid?);
        }
        var tokens = await ApiTokenVisibility.VisibleAsync(m_session, actor,
            visible.Select(row => row[7] as Guid?).Concat(published.Values.Select(x => x.ApiTokenId)), ct);
        var items = visible.Select((row, index) => new PageHistoryItem(
            (Guid)row[0], (int)row[1], (string)row[2], (Guid)row[3], authors[(Guid)row[3]],
            (DateTime)row[4], row[5] as DateTime?, (int)row[1] == page.Version || (Guid)row[0] == row[16] as Guid?,
            (Guid)row[0] == page.PublishedRevisionId || (Guid)row[0] == row[17] as Guid?,
            published.TryGetValue((Guid)row[0], out var last) ? last.At : null)
        {
            ChangeSummary = last?.Summary,
            Channel = ContentChannels.ForRead((string)row[6]),
            ApiToken = tokens.Find(row[7] as Guid?),
            PublicationChannel = ContentChannels.ForRead(last?.Channel),
            PublicationApiToken = tokens.Find(last?.ApiTokenId),
            GroupId = (Guid)row[8],
            // A step is measured against the step before it; the oldest step and a version against the previous version.
            PreviousRevisionId = query.Group is not null && index + 1 < rows.Count ? (Guid)rows[index + 1][0] : row[13] as Guid?,
            StartedAt = (DateTime)row[9],
            Checkpoints = (int)row[10],
            IsExplicit = (bool)row[11],
            Label = row[12] as string,
            IsAi = RevisionVersions.IsAi((string)row[6], (bool)row[14]),
            Changes = changes[index] is { } stored ? stored with
            {
                // Which blocks each checkpoint touched serves the requirement chip, not the history.
                Touched = [],
                Contributors = stored.Contributors.Select(x => x with { Name = authors.GetValueOrDefault(x.Id, "Neznámý autor") }).ToList()
            } : null
        }).ToArray();
        return new PageHistoryList(query.Id, page.Version, items, rows.Count > query.Limit ? items[^1].Version : null);
    }
}

