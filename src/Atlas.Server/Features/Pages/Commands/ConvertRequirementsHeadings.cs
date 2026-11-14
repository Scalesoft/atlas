using System.Text.Json;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

// Maintenance a migration will request and an administrator may run again from Úlohy (atlas_run_job), not a user's command: no MCP tool of its own.

/// <summary>One batch of <see cref="RequirementsHeadingsJob"/> over the checkpoint of the run <paramref name="RunId"/>; a dry run writes nothing.</summary>
public sealed record ConvertRequirementsHeadings(long RunId, bool DryRun);

/// <summary>The checkpoint of a run (JobRun.Progress): pages read up to <see cref="AfterPageId"/>, revisions rewritten (in a dry run: that
/// would be), pages deferred for their unsaved live edits, revisions left because they failed the gate or did not convert, and the
/// report's numbers: requirements before, reported cases, losses and gains no reported case explains; <see cref="Used"/> the approved
/// budget of cases by kind after the batch (<see cref="RequirementPageConversion.ApprovedCases"/>): the use shared with repair on open in a live
/// run, the dry run's own count in a dry run; null before the first batch. <see cref="Failed"/>: pages whose conversion threw (a timeout, an
/// error), skipped by the run and tried again by the next one.</summary>
public sealed record RequirementsHeadingsProgress(Guid AfterPageId, int Pages, int Revisions, int Deferred, int Refused, int Requirements, int Cases, int Losses, int Gains,
    IReadOnlyDictionary<string, int>? Used = null, int Failed = 0)
{
    public static readonly RequirementsHeadingsProgress Empty = new(Guid.Empty, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>What a batch read (0: the pass is done), the run's numbers after it and the batch's reports (one per revision read).</summary>
public sealed record RequirementsHeadingsRound(int Pages, RequirementsHeadingsProgress Progress, IReadOnlyList<RequirementConversionReport> Reports);

/// <summary>
/// A batch of the 3.6 conversion of requirement blocks to requirement headings (model §10b, the pattern of
/// <see cref="ConvertRequirementsOverviewHandler"/>): the next <see cref="BatchPages"/> page (one page, one short transaction: a heavy page
/// neither blocks nor rolls back the others, and the checkpoint moves past every page done) whose working or published revision holds a
/// <c>requirement</c> block and that no administrator rolled back, under their page locks, each such revision converted in place by
/// <see cref="RequirementPageConversion.ConvertPagesAsync"/>, the same function repair on open uses (gate, approved budget, backup, reset).
/// A page with unsaved live edits (the job stores those nobody is typing now before the batch, <see cref="UnsavedAsync"/>) waits for
/// another run <see cref="ConvertRequirementsOverviewHandler.PassDelay"/> later. A dry run reads
/// the same and writes only its own run's progress.
/// </summary>
public sealed class ConvertRequirementsHeadingsHandler(ISession session, IAtlasLocks locks, IJobRequests jobs, RequirementPageConversion conversion)
{
    public const int BatchPages = 1;

    public async Task<RequirementsHeadingsRound> Handle(ConvertRequirementsHeadings command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var progress = await ProgressAsync(session, command.RunId, ct);
        var batch = (await session.CreateSQLQuery(NextPages).AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("after", progress.AfterPageId).SetParameter("pages", BatchPages).ListAsync<Guid>(ct)).ToList();
        if (batch.Count == 0)
        {
            // Only pages with live activity in the last PassDelay come back by themselves (waiting helps); unsaved edits nobody is typing
            // now were stored before their batch (only those no author may save any more wait for the page's next editor; a checkpoint that
            // threw counts the page as failed), and a page that failed is in the run's summary and log and is
            // tried again by the next run (a deploy's or Úlohy „Spustit teď“), not every ten minutes.
            if (progress.Deferred > 0 && !command.DryRun && await ActiveAsync(ct))
            {
                await jobs.RequestAsync(RequirementsHeadingsJob.JobId, "Stránky s neuloženými živými úpravami",
                    notBefore: DateTime.UtcNow + ConvertRequirementsOverviewHandler.PassDelay, ct: ct);
            }
            return new RequirementsHeadingsRound(0, progress, []);
        }
        // A dry run takes no lock and changes nothing; its budget is the use so far, carried on by its checkpoint. A live batch waits for
        // the conversion's key first and then locks its pages (RequirementPageConversion: an open only tries the key, so nobody waits for
        // the batch while holding it).
        if (!command.DryRun)
        {
            await locks.AcquireAdvisoryAsync(RequirementPageConversion.LockKey, ct);
        }
        var pages = command.DryRun ? batch : await locks.LockPagesForUpdateAsync(batch, ct);
        var dryRunUse = command.DryRun ? new Dictionary<string, int>(progress.Used ?? await conversion.UseAsync(ct), StringComparer.Ordinal) : null;
        var result = await conversion.ConvertPagesAsync(pages, dryRunUse, ct);
        var reports = result.Reports;
        progress = new RequirementsHeadingsProgress(batch[^1], progress.Pages + batch.Count, progress.Revisions + result.Revisions, progress.Deferred + result.Deferred.Count,
            progress.Refused + result.Refused, progress.Requirements + reports.Sum(x => x.Before.Count), progress.Cases + reports.Sum(x => x.Cases.Count),
            progress.Losses + reports.Sum(x => x.Losses.Count(l => l.Accepted is null)), progress.Gains + reports.Sum(x => x.Gains.Count(g => g.Accepted is null)),
            dryRunUse ?? await conversion.UseAsync(ct), progress.Failed);
        await session.CreateSQLQuery("""UPDATE "JobRun" SET "Progress" = CAST(:progress AS jsonb) WHERE "Id" = :id""")
            .SetParameter("progress", JsonSerializer.Serialize(progress, JsonSerializerOptions.Web)).SetParameter("id", command.RunId).ExecuteUpdateAsync(ct);
        return new RequirementsHeadingsRound(batch.Count, progress, reports);
    }

    /// <summary>Whether a page still in the old shape had live activity in the last <see cref="ConvertRequirementsOverviewHandler.PassDelay"/>:
    /// its editors will have gone by the next run, so a follow-up converts it. Unsaved edits of nobody in particular do not change by waiting.</summary>
    private async Task<bool> ActiveAsync(CancellationToken ct)
        => await session.CreateSQLQuery($"""
                SELECT EXISTS (SELECT 1 FROM "PageCollaboration" c JOIN "PageRevision" r ON r."PageId" = c."PageId" JOIN "Page" p ON p."Id" = r."PageId"
                    WHERE c."UpdatedAt" > :recent AND {Current} AND {RequirementPageConversion.OldShape}
                      AND NOT EXISTS (SELECT 1 FROM "RequirementConversionBackup" b WHERE b."PageId" = r."PageId" AND b."RolledBack")) AS "Active"
                """).AddScalar("Active", NHibernateUtil.Boolean).SetParameter("recent", DateTime.UtcNow - ConvertRequirementsOverviewHandler.PassDelay, NHibernateUtil.UtcDateTime)
            .UniqueResultAsync<bool>(ct);

    /// <summary>The checkpoint past the next page, whose conversion threw in its own transaction (rolled back): counted as failed, left
    /// as stored, tried again by the next run. Only while the checkpoint is still <paramref name="before"/>, the one the failed batch started
    /// from: a batch that committed and failed after moved it, and then no page is skipped.</summary>
    public static async Task<RequirementsHeadingsProgress> SkipAsync(ISession session, long runId, Guid before, CancellationToken ct)
    {
        var progress = await ProgressAsync(session, runId, ct);
        if (progress.AfterPageId != before)
        {
            return progress;
        }
        var page = (await session.CreateSQLQuery(NextPages).AddScalar("PageId", NHibernateUtil.Guid)
            .SetParameter("after", progress.AfterPageId).SetParameter("pages", 1).ListAsync<Guid>(ct)).Cast<Guid?>().FirstOrDefault();
        progress = page is { } skipped ? progress with { AfterPageId = skipped, Pages = progress.Pages + 1, Failed = progress.Failed + 1 } : progress;
        await session.CreateSQLQuery("""UPDATE "JobRun" SET "Progress" = CAST(:progress AS jsonb) WHERE "Id" = :id""")
            .SetParameter("progress", JsonSerializer.Serialize(progress, JsonSerializerOptions.Web)).SetParameter("id", runId).ExecuteUpdateAsync(ct);
        return progress;
    }

    /// <summary>The live document of the next page after <paramref name="after"/> when it holds unsaved edits and had no activity in the
    /// last <see cref="ConvertRequirementsOverviewHandler.PassDelay"/>: <see cref="RequirementsHeadingsJob"/> checkpoints it before the batch.</summary>
    public static async Task<CheckpointLivePage?> UnsavedAsync(ISession session, Guid after, CancellationToken ct)
        => (await session.CreateSQLQuery($"""
                SELECT c."PageId", c."Generation" FROM "PageCollaboration" c
                WHERE c."PageId" = ({NextPages}) AND c."Sequence" > coalesce(c."LastSavedSequence", 0) AND c."UpdatedAt" <= :recent
                """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Generation", NHibernateUtil.Guid).SetParameter("after", after).SetParameter("pages", 1)
            .SetParameter("recent", DateTime.UtcNow - ConvertRequirementsOverviewHandler.PassDelay, NHibernateUtil.UtcDateTime).ListAsync<object[]>(ct))
            .Select(row => new CheckpointLivePage((Guid)row[0], (Guid)row[1])).FirstOrDefault();

    public static async Task<RequirementsHeadingsProgress> ProgressAsync(ISession session, long runId, CancellationToken ct)
        => await session.CreateSQLQuery("""SELECT CAST("Progress" AS text) AS "Progress" FROM "JobRun" WHERE "Id" = :id""")
            .AddScalar("Progress", NHibernateUtil.String).SetParameter("id", runId).UniqueResultAsync<string?>(ct) is { } json
            ? JsonSerializer.Deserialize<RequirementsHeadingsProgress>(json, JsonSerializerOptions.Web)!
            : RequirementsHeadingsProgress.Empty;

    /// <summary>A working or published revision of a page not purged (in the trash too, a template too).</summary>
    private const string Current = RequirementPageConversion.Current;

    /// <summary>The first <c>:pages</c> pages after <c>:after</c> by id with a current revision in an old shape (a requirement block or
    /// version 3, <see cref="RequirementPageConversion.Holds"/>) and no rolled-back conversion.</summary>
    private const string NextPages = $"""
        SELECT DISTINCT r."PageId" FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
        WHERE {Current} AND {RequirementPageConversion.OldShape} AND r."PageId" > :after
          AND NOT EXISTS (SELECT 1 FROM "RequirementConversionBackup" b WHERE b."PageId" = r."PageId" AND b."RolledBack")
        ORDER BY r."PageId" LIMIT :pages
        """;
}
