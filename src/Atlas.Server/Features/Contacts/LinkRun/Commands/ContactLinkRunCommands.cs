using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.LinkRun.Commands;

// The link migration of the contacts written into pages (contacts spec §6, K4) is a WikiAdmin's maintenance of the web's settings, not
// a user's command on an object: no MCP tool (ModuleCommandCoverageTests lists Contacts.Commands only). Its run is the job contacts.link.

/// <summary>Queues <c>apply</c> as the job <see cref="ContactLinkJob.JobId"/>, run as the admin who asks; the run plans and creates the
/// contacts and links the revisions, its report empty until its plan is done. A run already queued or running is answered as it is.</summary>
public sealed record StartContactLinkRun;

/// <summary>The next round of the run <paramref name="RunId"/>, for <see cref="ContactLinkJob"/> as the admin who started it: first a step
/// of the plan and its contacts, then a batch of pages; the pages it read (1 for a step of the plan), 0 when the run is done.</summary>
public sealed record ContinueContactLinkRun(long RunId);

/// <summary>A WikiAdmin stops the queued or running run: it ends <c>cancelled</c>, a running one at its next round. What it created and wrote
/// stays; the run answers as it is.</summary>
public sealed record CancelContactLinkRun;

public sealed class StartContactLinkRunHandler(ISession session, IContentAccess access, SpaceRoles roles, ContactDirectory directory, IJobControl jobs)
{
    public async Task<ContactLinkRunView> Handle(StartContactLinkRun command, CancellationToken ct)
    {
        var actor = await roles.RequireWikiAdminAsync(await access.LockActorAsync(null, ct), ct);
        // Under the directory's lock: a second start waits and finds this one queued. The run plans in its own bounded rounds, not here.
        await directory.LockAsync(ct);
        if (await ContactLinkRuns.NewestAsync(session, ct) is not { State: JobStates.Queued or JobStates.Running })
        {
            await jobs.RunNowAsync(ContactLinkJob.JobId, actor.Id, $"Spustil {actor.DisplayName}", ct);
            await session.FlushAsync(ct);
        }
        return (await ContactLinkRuns.NewestAsync(session, ct))!;
    }
}

public sealed class ContinueContactLinkRunHandler(ISession session, IAtlasLocks locks, ContactLinkMigration migration, IJobRequests jobs)
{
    public async Task<ContactLinkRound> Handle(ContinueContactLinkRun command, CancellationToken ct)
    {
        // The content lock before the rows of the pages, as a save takes them (LockActorAsync, then the page).
        await locks.AcquireContentSharedAsync(ct);
        var progress = await ContactLinkRuns.ProgressAsync(session, command.RunId, ct);
        if (progress.Identities is null)
        {
            // The plan, a bounded step a round from its checkpoint: the directory as it is now decides, and the new contacts are created
            // as the admin who started the run, under the directory's lock for this round only.
            var plan = await migration.PrepareAsync(progress.Preparation ?? ContactLinkPreparation.Start(), true, ContactLinkMigration.BatchPages, ct);
            progress = plan.Done
                ? progress with { Identities = plan.Ids, Report = plan.ToReport("apply"), Preparation = null }
                : progress with { Preparation = plan };
            await ContactLinkRuns.SaveAsync(session, command.RunId, progress, ct);
            return new ContactLinkRound(1, progress.ToSummary());
        }
        var (pages, last, written) = await migration.LinkBatchAsync(progress.Identities, progress.AfterPageId, ContactLinkMigration.BatchPages, ct);
        if (pages == 0)
        {
            if (progress.Revisions > 0)
            {
                await jobs.RequestAsync(Entities.EntityCatalogRebuildJob.JobId, "Propojení kontaktů", ct: ct);
            }
            return new ContactLinkRound(0, progress.ToSummary());
        }
        progress = progress with { AfterPageId = last, PagesDone = progress.PagesDone + pages, Revisions = progress.Revisions + written };
        await ContactLinkRuns.SaveAsync(session, command.RunId, progress, ct);
        return new ContactLinkRound(pages, progress.ToSummary());
    }
}

public sealed class CancelContactLinkRunHandler(ISession session, IContentAccess access, SpaceRoles roles, IJobControl jobs)
{
    public async Task<ContactLinkRunView?> Handle(CancelContactLinkRun command, CancellationToken ct)
    {
        var actor = await roles.RequireWikiAdminAsync(await access.LockActorAsync(null, ct), ct);
        if (await ContactLinkRuns.NewestAsync(session, ct) is { State: JobStates.Queued or JobStates.Running } run)
        {
            await jobs.CancelAsync(run.Id, actor.Id, ct);
        }
        return await ContactLinkRuns.NewestAsync(session, ct);
    }
}
