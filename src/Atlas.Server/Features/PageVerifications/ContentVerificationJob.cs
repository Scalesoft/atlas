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
/// again starts at stage 0 (PageVerificationHandler). Only verified pages (interval on, not a template, untrashed) with an active steward.
/// </summary>
public sealed class SendVerificationNoticesHandler(ISession session, Outbox outbox)
{
    public const int Batch = 500;

    /// <returns>The pages of this batch; fewer than <see cref="Batch"/> means nothing is left.</returns>
    public async Task<int> Handle(SendVerificationNotices command, CancellationToken ct)
    {
        var due = await session.ReadRowsAsync($"""
                SELECT v."PageId" AS "PageId", v."StewardId" AS "StewardId", v."VerifiedUntil" AS "VerifiedUntil"
                FROM "PageVerification" v
                JOIN "Page" p ON p."Id"=v."PageId"
                JOIN "Space" s ON s."Id"=p."SpaceId"
                JOIN "UserAccount" u ON u."Id"=v."StewardId" AND u."IsActive"
                WHERE NOT p."IsTrashed" AND p."PurgedAt" IS NULL AND s."TrashedAt" IS NULL AND {PageVerificationStates.IntervalSql}>0
                  AND (v."VerifiedUntil"<=:now AND v."NoticeStage"<2 OR v."VerifiedUntil"<=:soon AND v."NoticeStage"<1)
                ORDER BY v."PageId" LIMIT :batch
                FOR UPDATE OF v SKIP LOCKED
                """, new Dictionary<string, object> { ["now"] = command.Now, ["soon"] = command.Now + PageVerificationStates.Warning, ["batch"] = Batch },
            row => (PageId: row.GetGuid(0), StewardId: row.GetGuid(1), Until: DateTime.SpecifyKind(row.GetDateTime(2), DateTimeKind.Utc)), ct);
        foreach (var page in due)
        {
            var kind = page.Until <= command.Now ? NotificationKinds.VerificationExpired : NotificationKinds.VerificationExpiring;
            outbox.Notify(new NotificationRequested(kind, page.PageId, null, [page.StewardId]));
        }
        if (due.Count > 0)
        {
            await session.CreateSQLQuery("""
                    UPDATE "PageVerification" SET "NoticeStage"=CASE WHEN "VerifiedUntil"<=:now THEN 2 ELSE 1 END WHERE "PageId" IN (:pages)
                    """).SetParameter("now", command.Now, NHibernateUtil.UtcDateTime)
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
