using System.Text.Json;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Platform.Identity;
using Atlas.Server.Platform.Jobs;
using NHibernate.Linq;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.LinkRun;

/// <summary>A run of the link migration as the admin follows it: its progress over the pages holding a contact and what its first round decided.
/// <see cref="Id"/> is the JobRun, whose detail is general/jobs/contacts.link/runs/{id}.</summary>
/// <param name="Pages">The pages holding a contact when the run began; pages saved meanwhile may make <see cref="PagesDone"/> larger.</param>
/// <param name="Revisions">The revisions written so far.</param>
public sealed record ContactLinkRunView(long Id, string State, DateTime StartedAt, DateTime? FinishedAt, int Pages, int PagesDone, int Revisions,
    ContactLinkReport Report);

/// <summary>The run's checkpoint (JobRun.Progress): the pages done up to <see cref="AfterPageId"/> and what its plan decided
/// (<see cref="Identities"/> is null until then, while <see cref="Preparation"/> holds the plan's steps so far). A retried or resumed run goes on from it.</summary>
public sealed record ContactLinkProgress(Guid AfterPageId, int PagesDone, int Revisions, IReadOnlyDictionary<string, Guid>? Identities, ContactLinkReport Report,
    ContactLinkPreparation? Preparation = null)
{
    public static readonly ContactLinkProgress Empty = new(Guid.Empty, 0, 0, null, new ContactLinkReport("apply", [], [], [], 0, 0));

    public ContactLinkSummary ToSummary() => new(Report.Pages, PagesDone, Revisions, Report.Linked.Count, Report.Created.Count, Report.Unclear.Count, Report.Restricted?.Count ?? 0);
}

/// <summary>The numbers of the run page (JobRun.Summary).</summary>
public sealed record ContactLinkSummary(int Pages, int PagesDone, int Revisions, int Linked, int Created, int Unclear, int Restricted);

/// <summary>What one round answers: the pages it read (0: the run is done) and the numbers after it.</summary>
public sealed record ContactLinkRound(int Pages, ContactLinkSummary Summary);

/// <summary>The runs of the job <see cref="ContactLinkJob.JobId"/> (JobRun; its first form, the ContactLinkRun table of migration 2026103185,
/// stays one release read-only and is converted by migration 2026110450).</summary>
public static class ContactLinkRuns
{
    public static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>The queued or running run, else the newest; null before the first. A projection, so the row is the database's and not the
    /// instance a write of this transaction cached.</summary>
    public static async Task<ContactLinkRunView?> NewestAsync(ISession session, CancellationToken ct)
    {
        var runs = session.Query<JobRun>().Where(run => run.JobId == ContactLinkJob.JobId);
        var newest = await Rows(runs.Where(run => run.State == JobStates.Queued || run.State == JobStates.Running)).FirstOrDefaultAsync(ct)
            ?? await Rows(runs).FirstOrDefaultAsync(ct);
        if (newest is null)
        {
            return null;
        }
        // The page's poll reads only the numbers and the report, not the identities of the checkpoint.
        var progress = newest.Progress is null ? new ProgressView(0, 0, ContactLinkProgress.Empty.Report) : JsonSerializer.Deserialize<ProgressView>(newest.Progress, Json)!;
        return new ContactLinkRunView(newest.Id, newest.State, newest.StartedAt, newest.FinishedAt, progress.Report.Pages, progress.PagesDone, progress.Revisions, progress.Report);
    }

    public static async Task<ContactLinkProgress> ProgressAsync(ISession session, long runId, CancellationToken ct)
        => Read(await session.Query<JobRun>().Where(run => run.Id == runId).Select(run => run.Progress).SingleOrDefaultAsync(ct));

    /// <summary>Writes the checkpoint and the numbers into the run in the caller's transaction, so a batch and its position commit together.</summary>
    public static Task SaveAsync(ISession session, long runId, ContactLinkProgress progress, CancellationToken ct) => session.CreateSQLQuery("""
            UPDATE "JobRun" SET "Progress"=CAST(:progress AS jsonb),"Summary"=CAST(:summary AS jsonb) WHERE "Id"=:id
            """)
        .SetParameter("progress", JsonSerializer.Serialize(progress, Json)).SetParameter("summary", JsonSerializer.Serialize(progress.ToSummary(), Json))
        .SetParameter("id", runId).ExecuteUpdateAsync(ct);

    private static IQueryable<RunRow> Rows(IQueryable<JobRun> runs) => runs.OrderByDescending(run => run.Id)
        .Select(run => new RunRow(run.Id, run.State, run.StartedAt ?? run.QueuedAt, run.FinishedAt, run.Progress));

    private sealed record ProgressView(int PagesDone, int Revisions, ContactLinkReport Report);

    private sealed record RunRow(long Id, string State, DateTime StartedAt, DateTime? FinishedAt, string? Progress);

    private static ContactLinkProgress Read(string? json) => json is null ? ContactLinkProgress.Empty : JsonSerializer.Deserialize<ContactLinkProgress>(json, Json)!;
}

/// <summary>
/// The link migration of the contacts written into pages (contacts spec §6, K4) as a job (3.3 plan §7 wave 2): „Propojit kontakty“ queues it
/// (StartContactLinkRun, IJobControl.RunNowAsync) and it runs as the admin who started it (JobRun.RequestedBy): first the contacts, then a
/// batch of pages, each round its own transaction (ContinueContactLinkRun) holding the checkpoint (JobRun.Progress), repeated while there is
/// work. A stop request cancels the run before the next round; what it created and wrote stays. A round that fails is retried from the
/// checkpoint by the default JobRetry (five attempts, the five failures in a row of the old worker), and a run whose admin is gone fails at once.
/// </summary>
public sealed class ContactLinkJob : IJobDefinition
{
    public const string JobId = "contacts.link";

    public string Id => JobId;

    public string Title => "Propojení kontaktů ve stránkách";

    public string Description => "Propojí kontakty zapsané ve stránkách jen jako text s adresářem a pro neznámé osoby založí kontakty; spouští ji správce wiki.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var starter = context.RequestedBy is { } id ? await context.Services.GetRequiredService<ISession>().GetAsync<UserAccount>(id, ct) : null;
        if (starter is null)
        {
            throw new JobFailedException("Správce, který propojování spustil, už neexistuje.");
        }
        var bus = context.Services.GetRequiredService<IMessageBus>();
        ContactLinkRound round;
        // As the admin who started it, as the trash cleanup runs as who switched it on: the contacts it creates are theirs.
        using (ActingAccount.Use(starter.Id, starter.SecurityStamp))
        {
            do
            {
                // A step reads a stop request at once, the heartbeat only every 30 s; it stores the checkpoint as it is.
                await context.StepAsync((session, token) => ContactLinkRuns.ProgressAsync(session, context.RunId, token), ct);
                ct.ThrowIfCancellationRequested();
                round = await bus.InvokeAsync<ContactLinkRound>(new ContinueContactLinkRun(context.RunId), ct);
                context.Summarize(round.Summary);
            }
            while (round.Pages > 0);
        }
        context.Log.LogInformation("Propojování skončilo: {Pages} stránek, upraveno {Revisions} verzí.", round.Summary.PagesDone, round.Summary.Revisions);
    }
}
