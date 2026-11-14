using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.TrashCleanup.Contracts;
using Atlas.Server.Features.TrashCleanup.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TrashCleanup.Queries;

/// <summary>The rule in effect for one place: the wiki's own objects (SpaceId null) or a space. <see cref="ActorId"/> is who switched
/// it on, the account the automatic cleanup acts as; null for a manual rule.</summary>
public sealed record TrashCleanupRule(bool Automatic, int Days, bool DecidedByWiki, Guid? ActorId);

/// <summary>The current rows of the cleanup settings and the rule they make (unified trash spec §4b). No row: each space decides,
/// manually, after <see cref="DefaultDays"/>.</summary>
public static class CurrentTrashCleanup
{
    public const int DefaultDays = 60;
    public const int MinDays = 7;
    public const int MaxDays = 3650;

    public static Task<TrashCleanupSetting?> LoadWikiAsync(ISession session, CancellationToken ct)
        => session.QueryOver<TrashCleanupSetting>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static Task<SpaceTrashCleanupSetting?> LoadSpaceAsync(ISession session, Guid spaceId, CancellationToken ct)
        => session.QueryOver<SpaceTrashCleanupSetting>().Where(x => x.SpaceId == spaceId && x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    /// <summary>A space follows the wiki's rule when the wiki decides for all, its own otherwise; the wiki's own objects (no space) are
    /// cleaned only by a rule for all.</summary>
    public static TrashCleanupRule Rule(TrashCleanupSetting? wiki, SpaceTrashCleanupSetting? space, bool forSpace)
    {
        if (wiki is { Scope: TrashCleanupSetting.WikiScope })
        {
            return new TrashCleanupRule(wiki.Automatic, wiki.Days, true, wiki.Automatic ? wiki.ChangedById : null);
        }
        return forSpace
            ? new TrashCleanupRule(space?.Automatic ?? false, space?.Days ?? DefaultDays, false, space is { Automatic: true } ? space.ChangedById : null)
            : new TrashCleanupRule(false, DefaultDays, false, null);
    }

    public static WikiTrashCleanupView ToView(TrashCleanupSetting? row)
        => new(row?.Scope ?? TrashCleanupSetting.SpaceScope, row?.Automatic ?? false, row?.Days ?? DefaultDays,
            row is null ? null : AdministrationVersion.Format(row.ActiveFrom));

    public static void Validate(bool? automatic, int? days)
    {
        if (automatic is null)
        {
            throw new ApiException(400, "Zvolte, zda čistit ručně, nebo automaticky.");
        }
        if (days is not (>= MinDays and <= MaxDays))
        {
            throw new ApiException(400, "Počet dní musí být od {minDays} do {maxDays}.", "invalid_days", args: new Dictionary<string, object?> { ["minDays"] = MinDays, ["maxDays"] = MaxDays });
        }
    }
}

/// <summary>
/// What a cleanup takes (unified trash spec §4b) in one place, the space <c>:space</c> or the wiki's own objects (null): the items of
/// its trash (pages, boards, stored objects; never a private one, never a purged page's tombstone) and its stored objects nobody uses.
/// Unused is decided over every edge of the relation sources, never over what a reader sees: an object on a board its admin cannot
/// read is used. A trashed space is no place of a cleanup (its purge is a wiki admin's, by name).
/// </summary>
public static class TrashCleanupCandidates
{
    private const string Place = """
        (CAST(:space AS uuid) IS NULL AND r."Scope" = 'wiki' AND r."SpaceId" IS NULL OR r."Scope" = 'space' AND r."SpaceId" = CAST(:space AS uuid))
        """;

    /// <summary>Its trashed items, oldest first and a batch's top page before its subpages (whose purge it takes along); with
    /// <paramref name="before"/> only those due: trashed before it, less a merged contact stored content still names (its purge is refused,
    /// contact_merged_in_use, so it is no candidate until the content names the target).</summary>
    public static async Task<IReadOnlyList<Guid>> TrashedAsync(ISession session, Guid? spaceId, IReadOnlyCollection<string> types, DateTime? before, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
                SELECT r."Id", r."TypeId" = :contact AND COALESCE(r."Data" ->> 'mergedInto', '') <> '' AS "Merged" FROM "ContentResource" r
                LEFT JOIN "Page" p ON p."Id" = r."Id"
                WHERE {Place} AND r."TrashedAt" IS NOT NULL AND r."TypeId" = ANY(CAST(string_to_array(:types, ',') AS text[]))
                  AND p."PurgedAt" IS NULL AND (CAST(:before AS timestamptz) IS NULL OR r."TrashedAt" < CAST(:before AS timestamptz))
                ORDER BY r."TrashedAt", EXISTS (SELECT 1 FROM "Page" q WHERE q."Id" = p."ParentId" AND q."TrashBatchId" = p."TrashBatchId"), r."Id"
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Merged", NHibernateUtil.Boolean)
            .SetParameter("space", spaceId, NHibernateUtil.Guid).SetParameter("types", string.Join(',', types))
            .SetParameter("before", before, NHibernateUtil.UtcDateTime).SetParameter("contact", ContactObjectType.Id)
            .ListAsync<object[]>(ct);
        var merged = rows.Where(row => (bool)row[1]).Select(row => (Guid)row[0]).ToArray();
        var held = before is null || merged.Length == 0 ? [] : (await MentionedAsync(session, merged, ct)).ToHashSet();
        return rows.Select(row => (Guid)row[0]).Where(id => !held.Contains(id)).ToArray();
    }

    /// <summary>Its live stored objects unchanged since <paramref name="before"/> that nothing uses: no edge of a relation source and no
    /// stored content names them (<see cref="MentionedAsync"/>). Fails closed: a use no module reads still keeps an object.</summary>
    public static async Task<IReadOnlyList<Guid>> UnusedAsync(ISession session, ContentParticipants participants, Guid? spaceId,
        IReadOnlyCollection<string> storedTypes, DateTime before, CancellationToken ct, IReadOnlyCollection<Guid>? only = null)
    {
        var stale = await session.CreateSQLQuery($"""
                SELECT r."Id" FROM "ContentResource" r
                WHERE {Place} AND r."TrashedAt" IS NULL AND r."UpdatedAt" < :before
                  AND r."TypeId" = ANY(CAST(string_to_array(:types, ',') AS text[]))
                  AND (CAST(:only AS text) IS NULL OR r."Id" = ANY(CAST(string_to_array(:only, ',') AS uuid[])))
                ORDER BY r."UpdatedAt", r."Id"
                """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .SetParameter("space", spaceId, NHibernateUtil.Guid).SetParameter("types", string.Join(',', storedTypes))
            .SetParameter("before", before, NHibernateUtil.UtcDateTime)
            .SetParameter("only", only is null ? null : string.Join(',', only), NHibernateUtil.String)
            .ListAsync<Guid>(ct);
        if (stale.Count == 0)
        {
            return [];
        }
        var used = (await MentionedAsync(session, stale.ToArray(), ct)).ToHashSet();
        foreach (var edge in await RelationAnchor.ReadAllAsync(session, participants.Relations.Select(source => source.Statement).ToArray(),
            spaceId ?? Guid.Empty, stale.ToArray(), true, ct))
        {
            used.Add(RelationKinds.UsedEnd(edge));
        }
        return stale.Where(id => !used.Contains(id)).ToArray();
    }

    /// <summary>
    /// Of <paramref name="ids"/>, those any stored content names, whatever module reads it and whether it is enabled: a card or a block of
    /// a board not deleted (also on a board in the trash), the current draft or the published revision of a page not purged (templates,
    /// drafts and pages in the trash included), another object's data (a private note) and a tree reference (one at a space's root has
    /// the space as its source, so the relation sources leave it out). Deciding a permanent delete, anything that names it keeps it.
    /// So a merge target is named by its merged contact's data (mergedInto) and is never unused while that contact exists: wanted, the
    /// redirect of the content naming the merged one ends at it.
    /// </summary>
    // shortcut: text scans over the content of the whole wiki for each run and settings read; index the named ids (a derived table of
    // occurrences) when a wiki grows past a few thousand pages and this shows in the response time.
    public static async Task<IReadOnlyList<Guid>> MentionedAsync(ISession session, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        return (await session.CreateSQLQuery($"""
                SELECT x.id FROM unnest(CAST(string_to_array(:ids, ',') AS uuid[])) AS x(id), LATERAL (SELECT '%' || CAST(x.id AS text) || '%' AS pattern) q
                WHERE EXISTS (SELECT 1 FROM "BoardItem" i WHERE NOT i."IsDeleted" AND (i."TargetResourceId" = x.id OR CAST(i."PropsJson" AS text) ILIKE q.pattern))
                   OR EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" v ON v."PageId" = p."Id" AND (v."ActiveTo" IS NULL OR v."Id" = p."PublishedRevisionId")
                              WHERE p."PurgedAt" IS NULL AND (v."ContentJson" ILIKE q.pattern OR v."Markdown" ILIKE q.pattern))
                   OR EXISTS (SELECT 1 FROM "ContentResource" o WHERE o."Id" <> x.id AND CAST(o."Data" AS text) ILIKE q.pattern)
                   OR EXISTS (SELECT 1 FROM "TreeReference" t WHERE NOT t."IsTrashed" AND t."TargetId" = x.id)
                """).AddScalar("id", NHibernateUtil.Guid).SetParameter("ids", string.Join(',', ids)).ListAsync<Guid>(ct)).ToArray();
    }

    public static async Task<TrashCleanupCounts> CountAsync(ISession session, ContentParticipants participants, ObjectTypes types, Guid? spaceId,
        int days, DateTime now, CancellationToken ct)
    {
        var before = now.AddDays(-days);
        var trashed = await TrashedAsync(session, spaceId, types.TypeIds(), null, ct);
        var due = await TrashedAsync(session, spaceId, types.TypeIds(), before, ct);
        var unused = await UnusedAsync(session, participants, spaceId, types.StoredTypeIds(), before, ct);
        return new TrashCleanupCounts(trashed.Count, due.Count, unused.Count);
    }
}
