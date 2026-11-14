using Atlas.Server.Features.Jobs.Queries;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Jobs.Commands;

/// <summary>„Spustit teď“: runs the job at once, even a paused one (the explicit will of the administrator). The wiki administrator's
/// only: a new run carries no space, so it would run over every space (a retry keeps its run's space).</summary>
public sealed record RunJobNow(string JobId);

/// <summary>„Zkusit znovu“: the failed or stopped run again, from its checkpoint.</summary>
public sealed record RetryJobRun(string JobId, long RunId);

/// <summary>„Zastavit“: a queued run ends at once, a running one at its next step; what it committed stays.</summary>
public sealed record CancelJobRun(string JobId, long RunId);

/// <summary>Pauses or resumes every trigger of the job; the wiki administrator's only, as the module settings.</summary>
public sealed record SetJobPaused(string JobId, bool Paused);

/// <summary>The administrator's actions on jobs (3.3 plan §4.6). The rights are checked here, IJobControl does the work in this transaction.
/// IJobControl is asked of the provider: it is the job runtime's, registered where that runs.</summary>
public sealed class JobCommandHandler(ISession session, IContentAccess access, SpaceRoles roles, IServiceProvider services)
{
    public async Task<JobRunRef> Handle(RunJobNow command, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        viewer.RequireWikiAdmin();
        var job = await JobLookup.FindAsync(session, services, viewer, command.JobId, ct);
        if (!job.Known)
        {
            throw new ApiException(409, "Tuto úlohu už Atlas nezná, nejde spustit.", "job_unknown");
        }
        return new JobRunRef(await Control.RunNowAsync(job.Id, viewer.AccountId, $"Spustil(a) {viewer.Name}", ct));
    }

    public async Task<JobRunRef> Handle(RetryJobRun command, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        var run = await JobLookup.RunAsync(session, services, viewer, command.JobId, command.RunId, ct);
        if (run.State is not (JobStates.Failed or JobStates.Cancelled))
        {
            throw new ApiException(409, "Znovu jde spustit jen selhaný nebo zastavený běh.", "run_not_retryable");
        }
        return new JobRunRef(await Control.RetryAsync(run.Id, viewer.AccountId, ct));
    }

    public async Task<JobRunRef> Handle(CancelJobRun command, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        var run = await JobLookup.RunAsync(session, services, viewer, command.JobId, command.RunId, ct);
        if (run.State is not (JobStates.Queued or JobStates.Running))
        {
            throw new ApiException(409, "Běh už skončil.", "run_finished");
        }
        await Control.CancelAsync(run.Id, viewer.AccountId, ct);
        return new JobRunRef(run.Id);
    }

    public async Task Handle(SetJobPaused command, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        viewer.RequireWikiAdmin();
        var job = await JobLookup.FindAsync(session, services, viewer, command.JobId, ct);
        await Control.SetPausedAsync(job.Id, command.Paused, viewer.AccountId, ct);
    }

    private IJobControl Control => services.GetRequiredService<IJobControl>();
}
