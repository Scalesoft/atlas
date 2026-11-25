using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageVerifications.Domain;
using Atlas.Server.Platform.Events;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications.Commands;

/// <summary>Ověřit, Nastavit garanta, interval of one page (plan §2.3): anyone who may edit the page (an editor or the space's admin).</summary>
public sealed record SetPageVerification(Guid PageId, SetPageVerificationRequest Request);

/// <summary>The bulk actions of Stav obsahu over pages of one space; each page is decided as <see cref="SetPageVerification"/>, except that
/// stewardFromLastAuthor skips a page that has an active steward.</summary>
public sealed record SetSpaceVerification(Guid SpaceId, SetSpaceVerificationRequest Request);

/// <summary>„Nahlásit neaktuální“: any reader of the page tells its steward; nothing on the page changes.</summary>
public sealed record ReportPageOutdated(Guid PageId, ReportPageOutdatedRequest Request);

/// <summary>atlas_set_verification: G5 first (the page's space, or the space of a bulk change), then the shared write.</summary>
public sealed record SetMcpVerification(Guid PageId, SetPageVerificationRequest Request);

/// <summary>atlas_set_verification with spaceId: G5 on the space, then the shared bulk write.</summary>
public sealed record SetMcpSpaceVerification(Guid SpaceId, SetSpaceVerificationRequest Request);

/// <summary>atlas_report_outdated: G5 on the page, then the shared report.</summary>
public sealed record ReportMcpOutdated(ReportPageOutdated Command);

public sealed class PageVerificationHandler(ISession session, OrganizationLocks locks, IContentAccess access, PageGate gate,
    PageVerificationReader reader, IRequestChannel channel, Outbox outbox, TimeProvider clock)
{
    public const int MaxBulk = 500;
    public const int MaxNote = 500;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    public async Task<PageVerificationView> Handle(SetPageVerification command, CancellationToken ct)
    {
        // A readable page without EditContent answers 403, anything else 404.
        var (page, actor) = await locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        await ApplyAsync(page, actor, command.Request, ct);
        await session.FlushAsync(ct);
        return await reader.ReadAsync(page.Id, await PublishedVersionAsync(page, ct), ct);
    }

    public async Task<SpaceVerificationResult> Handle(SetSpaceVerification command, CancellationToken ct)
    {
        var request = command.Request;
        var space = await session.GetAsync<Space>(command.SpaceId, ct);
        if (space is null || space.IsTrashed)
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        // Stav obsahu's writers only, decided before any page is selected or locked (the space row first: the lock order of every write).
        var actor = await locks.LockForWriteAsync(space.Id, ct);
        await access.RequireSpaceAsync(space.Id, SpaceRole.Writer, ct);
        List<Guid> ids;
        if (request.PageIds is null)
        {
            if (request.Action != "stewardFromLastAuthor")
            {
                throw new ApiException(400, "Bez výběru stránek lze jen nastavit garanta podle posledního autora.");
            }
            ids = await WithoutStewardAsync(space, actor, ct);
        }
        else
        {
            ids = request.PageIds.Distinct().ToList();
        }
        if (ids.Count > MaxBulk)
        {
            throw new ApiException(400, "Najednou lze změnit nejvýše {max} stránek.", args: new Dictionary<string, object?> { ["max"] = MaxBulk });
        }
        // A page of another space is missing here: skipped before anything locks it or its space.
        var inSpace = ids.Count == 0 ? [] : (await session.QueryOver<Page>().Where(x => x.SpaceId == space.Id).WhereRestrictionOn(x => x.Id).IsIn(ids)
            .Select(x => x.Id).ListAsync<Guid>(ct)).ToHashSet();
        var single = request.Action switch
        {
            "verify" => new SetPageVerificationRequest(Verify: true),
            "steward" => request.StewardId is { } steward ? new SetPageVerificationRequest(StewardId: steward) : new SetPageVerificationRequest(ClearSteward: true),
            "stewardFromLastAuthor" => new SetPageVerificationRequest(StewardFromLastAuthor: true),
            _ => throw new ApiException(400, "Neznámá hromadná akce ověření.")
        };
        var updated = 0;
        var skipped = new List<SkippedPage>();
        foreach (var id in ids)
        {
            if (!inSpace.Contains(id))
            {
                skipped.Add(new SkippedPage(id, "404"));
                continue;
            }
            try
            {
                var (page, _) = await locks.LockPageAsync(id, false, AccessOperation.EditContent, ct);
                if (page.SpaceId != command.SpaceId)
                {
                    skipped.Add(new SkippedPage(id, "404"));
                    continue;
                }
                // „Garant podle posledního autora“ fills in a missing steward, it never replaces one.
                if (request.Action == "stewardFromLastAuthor" && await HasStewardAsync(page.Id, ct))
                {
                    skipped.Add(new SkippedPage(id, "has_steward"));
                    continue;
                }
                await ApplyAsync(page, actor, single, ct);
                updated++;
            }
            catch (ApiException error) when (error.Status is 400 or 403 or 404)
            {
                // Refused before anything was written: the page stays, the rest goes on.
                skipped.Add(new SkippedPage(id, error.Code ?? error.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        await session.FlushAsync(ct);
        return new SpaceVerificationResult(updated, skipped);
    }

    public async Task<PageOutdatedReport> Handle(ReportPageOutdated command, CancellationToken ct)
    {
        var opened = (await gate.ReadAsync(command.PageId, AccessOperation.ReadContent, ct)).Require();
        var note = Rules.OptionalText(command.Request.Note, "Poznámka", MaxNote, null);
        var row = await session.GetAsync<PageVerification>(opened.Page.Id, ct);
        var steward = row?.StewardId is { } id ? await session.GetAsync<UserAccount>(id, ct) : null;
        IReadOnlyList<Guid> recipients = steward is { IsActive: true }
            ? [steward.Id]
            : (await session.CreateSQLQuery("""
                    SELECT x."PublisherId" AS "PublisherId" FROM (
                      SELECT p."PublisherId" AS "PublisherId", max(p."ActiveFrom") AS "Last" FROM "PagePublication" p
                      JOIN "UserAccount" u ON u."Id"=p."PublisherId" AND u."IsActive"
                      WHERE p."PageId"=:page GROUP BY p."PublisherId") x
                    ORDER BY x."Last" DESC LIMIT 3
                    """).AddScalar("PublisherId", NHibernateUtil.Guid).SetParameter("page", opened.Page.Id).ListAsync<Guid>(ct)).ToList();
        var told = recipients.Where(x => x != opened.Actor.Id).ToList();
        outbox.Notify(new NotificationRequested(NotificationKinds.ReportedOutdated, opened.Page.Id, opened.Actor.Id, told, string.IsNullOrEmpty(note) ? null : note));
        return new PageOutdatedReport(told.Count);
    }

    /// <summary>Applies the request to a page locked for editing; throws a 400 before anything changes.</summary>
    private async Task ApplyAsync(Page page, UserAccount actor, SetPageVerificationRequest request, CancellationToken ct)
    {
        if (request is { Verify: not true, StewardId: null, StewardFromLastAuthor: not true, IntervalMonths: null, InheritInterval: not true, ClearSteward: not true })
        {
            throw new ApiException(400, "Zadejte, co se má u ověření stránky změnit.");
        }
        if (page.IsTemplate)
        {
            throw new ApiException(400, "Šablony se neověřují.");
        }
        if (request.IntervalMonths is { } months && months != 0 && !PageVerificationStates.Intervals.Contains(months))
        {
            throw new ApiException(400, "Vyberte interval ověření 3, 6 nebo 12 měsíců, nebo stránku neověřovat.");
        }
        if (request.Until is not null && request.Verify != true)
        {
            throw new ApiException(400, "Datum platnosti lze zadat jen s ověřením.");
        }
        var row = await session.GetAsync<PageVerification>(page.Id, ct);
        var isNew = row is null;
        row ??= new PageVerification { PageId = page.Id };
        var steward = row.StewardId;
        if (request.InheritInterval == true)
        {
            row.IntervalMonths = null;
        }
        else if (request.IntervalMonths is { } interval)
        {
            row.IntervalMonths = (short)interval;
        }
        if (request.StewardFromLastAuthor == true)
        {
            row.StewardId = await RequireStewardAsync(page, await LastAuthorAsync(page, ct)
                ?? throw new ApiException(400, "Stránka nemá publikovanou verzi, podle jejíhož autora by se garant nastavil."), ct);
        }
        if (request.ClearSteward == true)
        {
            row.StewardId = null;
        }
        if (request.StewardId is { } stewardId)
        {
            row.StewardId = await RequireStewardAsync(page, stewardId, ct);
        }
        if (row.StewardId != steward)
        {
            // A new steward has had no reminder yet: the job tells them the stage the previous one got.
            row.NoticeStage = PageVerificationStates.NoticeNone;
        }
        if (request.Verify == true)
        {
            var space = await session.GetAsync<Space>(page.SpaceId, ct);
            if (PageVerificationStates.Interval(row.IntervalMonths, space?.VerificationIntervalMonths, page.IsTemplate) is not ({ } interval and > 0))
            {
                throw new ApiException(400, "Ověřování je u této stránky vypnuté.");
            }
            var published = await PublishedVersionAsync(page, ct)
                ?? throw new ApiException(400, "Ověřit lze jen publikovanou stránku. Nejdřív ji publikujte.", "not_published");
            var now = clock.GetUtcNow().UtcDateTime;
            var until = request.Until is { } chosen ? Until(chosen) : now.AddMonths(interval);
            if (until <= now || until > now.AddYears(5))
            {
                throw new ApiException(400, "Ověřit lze do data v budoucnosti, nejvýše za pět let.");
            }
            row.VerifiedById = actor.Id;
            row.VerifiedAt = now;
            row.VerifiedUntil = until;
            row.VerifiedVersion = published;
            row.VerifiedChannel = channel.Current.Code;
            row.NoticeStage = PageVerificationStates.NoticeNone;
        }
        if (isNew)
        {
            await session.SaveAsync(row, ct);
        }
    }

    /// <summary>The steward must be an active account that may read the page (O4: one without edit rights is reminded, but cannot verify).</summary>
    private async Task<Guid> RequireStewardAsync(Page page, Guid stewardId, CancellationToken ct)
    {
        var reads = await session.CreateSQLQuery("""
                SELECT EXISTS (SELECT 1 FROM "EffectivePageAccess" e JOIN "UserAccount" u ON u."Id"=e."UserId"
                  WHERE e."UserId"=:user AND e."PageId"=:page AND e."Role">=1 AND u."IsActive") AS "Reads"
                """).AddScalar("Reads", NHibernateUtil.Boolean).SetParameter("user", stewardId).SetParameter("page", page.Id).UniqueResultAsync<bool>(ct);
        return reads ? stewardId : throw new ApiException(400, "Tento uživatel nemůže být garantem této stránky.");
    }

    /// <summary>„Ověřit do“: a time with a zone (Z or an offset) as it is; one without a zone in Europe/Prague, whatever the server's own
    /// zone, and a bare date (the web's date field, „2027-03-01“ over MCP) valid through the end of that day.</summary>
    public static DateTime Until(DateTime chosen) => chosen.Kind != DateTimeKind.Unspecified
        ? chosen.ToUniversalTime()
        : TimeZoneInfo.ConvertTimeToUtc(chosen.TimeOfDay == TimeSpan.Zero ? chosen.AddDays(1).AddTicks(-10) : chosen, Zone);

    private async Task<bool> HasStewardAsync(Guid pageId, CancellationToken ct)
        => (await session.GetAsync<PageVerification>(pageId, ct))?.StewardId is { } steward && (await session.GetAsync<UserAccount>(steward, ct))?.IsActive == true;

    /// <summary>The author of the last published version (PageRevision.AuthorId), null when never published.</summary>
    private async Task<Guid?> LastAuthorAsync(Page page, CancellationToken ct)
        => page.PublishedRevisionId is { } revision
            ? await session.QueryOver<PageRevision>().Where(x => x.Id == revision).Select(x => x.AuthorId).SingleOrDefaultAsync<Guid?>(ct)
            : null;

    private async Task<int?> PublishedVersionAsync(Page page, CancellationToken ct)
        => page.PublishedRevisionId is { } revision
            ? await session.QueryOver<PageRevision>().Where(x => x.Id == revision).Select(x => x.Version).SingleOrDefaultAsync<int?>(ct)
            : null;

    /// <summary>The space's published pages that are verified (own or inherited interval), have no active steward and the actor may edit
    /// (EffectivePageAccess), at most <see cref="MaxBulk"/>; each is checked again under its lock.</summary>
    private async Task<List<Guid>> WithoutStewardAsync(Space space, UserAccount actor, CancellationToken ct)
        => (await session.CreateSQLQuery($"""
                SELECT p."Id" AS "Id" FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId"
                JOIN "EffectivePageAccess" e ON e."PageId"=p."Id" AND e."UserId"=:actor AND e."Role">=2
                LEFT JOIN "PageVerification" v ON v."PageId"=p."Id"
                LEFT JOIN "UserAccount" st ON st."Id"=v."StewardId" AND st."IsActive"
                WHERE p."SpaceId"=:space AND NOT p."IsTrashed" AND p."PublishedRevisionId" IS NOT NULL
                  AND {PageVerificationStates.IntervalSql}>0 AND st."Id" IS NULL
                ORDER BY p."Id" LIMIT :limit
                """).AddScalar("Id", NHibernateUtil.Guid).SetParameter("space", space.Id).SetParameter("actor", actor.Id)
            .SetParameter("limit", MaxBulk).ListAsync<Guid>(ct)).ToList();
}

/// <summary>5.2 (plan §2.3): a new page of a space that verifies its pages starts with its author as the steward, unverified.</summary>
public static class PageVerificationStart
{
    public static async Task StartAsync(ISession session, Page page, CancellationToken ct)
    {
        if (page.IsTemplate || (await session.GetAsync<Space>(page.SpaceId, ct))?.VerificationIntervalMonths is null)
        {
            return;
        }
        await session.SaveAsync(new PageVerification { PageId = page.Id, StewardId = page.OwnerId }, ct);
    }
}
