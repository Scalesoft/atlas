using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.PageVerifications.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using NHibernate;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications;

/// <summary>One batch of the reminders (plan §4): the job's own message, not a user command.</summary>
public sealed record SendVerificationNotices(DateTime Now);

/// <summary>
/// The stewards of pages whose verification expires within 14 days or has expired get one notice per stage (O3), with the stage written
/// in the same transaction as the outbox message: a repeated run, or a run after a crash, sends nothing a page has had. A page verified
/// again starts at stage 0 (PageVerificationHandler). Only verified pages (interval on, not a template, untrashed) with a steward in effect (active and able to read the page).
/// </summary>
public sealed class SendVerificationNoticesHandler(ISession session, Outbox outbox)
{
    public const int Batch = 500;

    /// <returns>The pages of this batch; fewer than <see cref="Batch"/> means nothing is left.</returns>
    public async Task<int> Handle(SendVerificationNotices command, CancellationToken ct)
    {
        // By Prague days (O3): „vypršelo“ on the day the verification ends, „brzy vyprší“ 14 days before it, whatever its time of day.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(command.Now, Queries.SpaceReadership.Prague));
        var end = PersonalLibrary.AnalyticsHandler.Period(today, today).End;
        var soon = PersonalLibrary.AnalyticsHandler.Period(today, today.AddDays(PageVerificationStates.Warning.Days)).End;
        var due = await session.ReadRowsAsync($"""
                SELECT v."PageId" AS "PageId", v."StewardId" AS "StewardId", v."VerifiedUntil" AS "VerifiedUntil"
                FROM "PageVerification" v
                JOIN "Page" p ON p."Id"=v."PageId"
                JOIN "Space" s ON s."Id"=p."SpaceId"
                {PageVerificationStates.StewardJoinSql}
                WHERE st."Id" IS NOT NULL AND NOT p."IsTrashed" AND p."PurgedAt" IS NULL AND s."TrashedAt" IS NULL AND {PageVerificationStates.IntervalSql}>0
                  AND (v."VerifiedUntil"<:end AND v."NoticeStage"<{PageVerificationStates.NoticeExpired}
                    OR v."VerifiedUntil"<:soon AND v."NoticeStage"<{PageVerificationStates.NoticeExpiring})
                ORDER BY v."PageId" LIMIT :batch
                FOR UPDATE OF v SKIP LOCKED
                """, new Dictionary<string, object> { ["end"] = end, ["soon"] = soon, ["batch"] = Batch },
            row => (PageId: row.GetGuid(0), StewardId: row.GetGuid(1), Until: DateTime.SpecifyKind(row.GetDateTime(2), DateTimeKind.Utc)), ct);
        foreach (var page in due)
        {
            var kind = page.Until < end ? NotificationKinds.VerificationExpired : NotificationKinds.VerificationExpiring;
            outbox.Notify(new NotificationRequested(kind, page.PageId, null, [page.StewardId]));
        }
        if (due.Count > 0)
        {
            await session.CreateSQLQuery($"""
                    UPDATE "PageVerification" SET "NoticeStage"=CASE WHEN "VerifiedUntil"<:end THEN {PageVerificationStates.NoticeExpired} ELSE {PageVerificationStates.NoticeExpiring} END
                    WHERE "PageId" IN (:pages)
                    """).SetParameter("end", end, NHibernateUtil.UtcDateTime)
                .SetParameterList("pages", due.Select(x => x.PageId).ToList()).ExecuteUpdateAsync(ct);
        }
        return due.Count;
    }
}

/// <summary>Every morning before working hours (UTC): the reminders of <see cref="SendVerificationNoticesHandler"/>, batch by batch.</summary>
public sealed class ContentVerificationJob(TimeProvider clock) : IJobDefinition
{
    public string Id => "content-verification";

    public string Title => "Připomínky ověření obsahu";

    public string Description => "Garantům stránek pošle oznámení 14 dní před vypršením ověření a v den vypršení, každé jen jednou.";

    public JobSchedule? Schedule => JobSchedule.Cron("15 5 * * *");

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        var now = clock.GetUtcNow().UtcDateTime;
        var pages = 0;
        int batch;
        do
        {
            batch = await bus.InvokeAsync<int>(new SendVerificationNotices(now), ct);
            pages += batch;
        }
        while (batch == SendVerificationNoticesHandler.Batch);
        context.Summarize(new { pages });
    }
}
