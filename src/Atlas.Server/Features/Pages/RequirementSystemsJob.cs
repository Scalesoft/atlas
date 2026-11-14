using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.Pages;

/// <summary>
/// The 4.0 fill of requirement services from the catalog (docs/background-jobs.md „Migrace dat mezi verzemi“): migration 2026111150
/// requests one run where a page is marked as a catalog. Batches of <see cref="FillRequirementSystemsHandler"/>, each its own transaction
/// holding the checkpoint, while there is work; a stop request ends the run before the next batch and what committed stays. It fills
/// only what is empty, so another run (Úlohy „Spustit teď“, atlas_run_job, say after the catalog named more services) changes only
/// requirements still without a service; the restore of a backup older than the migration requests it again (InstallationModules.DataMigrations).
/// </summary>
public sealed class RequirementSystemsJob : IJobDefinition
{
    public const string JobId = "pages.requirement-systems";

    public string Id => JobId;

    public string Title => "Doplnění služeb požadavků z katalogu";

    public string Description => "Požadavkům vloženým z katalogu, které nemají vyplněnou službu (system), doplní službu jejich položky katalogu, aby je tabule nasazení kreslila jako externí služby. Vyplněné hodnoty nemění; po doplnění služeb v katalogu ji lze spustit znovu.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        RequirementSystemsRound round;
        do
        {
            // A step reads a stop request at once, the heartbeat only every 30 s; it stores the checkpoint as it is.
            var current = await context.StepAsync((session, token) => FillRequirementSystemsHandler.ProgressAsync(session, context.RunId, token), ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                // Unsaved live edits nobody is typing now are stored first, as the heading conversion does (RequirementsHeadingsJob); the same
                // page again: its checkpoint stored nothing (no author may save it), and the batch defers it.
                if (await FillRequirementSystemsHandler.UnsavedAsync(context.Services.GetRequiredService<NHibernate.ISession>(), context.RunId, ct) is { } unsaved)
                {
                    context.Log.LogInformation("Stránka {PageId}: neuložené živé úpravy se ukládají před doplněním.", unsaved.PageId);
                    await bus.InvokeAsync(unsaved, ct);
                }
                round = await bus.InvokeAsync<RequirementSystemsRound>(new FillRequirementSystems(context.RunId), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One page, one transaction: its failure rolled back that page only; the run goes on past it.
                var skipped = await context.StepAsync((session, token) => FillRequirementSystemsHandler.SkipAsync(session, context.RunId, current.AfterPageId, token), ct);
                var moved = skipped.AfterPageId != current.AfterPageId;
                context.Log.LogWarning("Stránka {PageId} se nedoplnila ({Error}); běh pokračuje, další běh ji zkusí znovu.", moved ? skipped.AfterPageId : null,
                    error.GetType().Name + ": " + error.Message);
                round = new RequirementSystemsRound(moved ? 1 : 0, skipped);
            }
            context.Summarize(round.Progress);
        }
        while (round.Pages > 0);
        context.Log.LogInformation("Doplnění skončilo: {Pages} stránek, přepsáno {Revisions} verzí, doplněno {Filled} požadavků, odloženo {Deferred} stránek, ponecháno {Refused} verzí, selhalo {Failed} stránek.",
            round.Progress.Pages, round.Progress.Revisions, round.Progress.Filled, round.Progress.Deferred, round.Progress.Refused, round.Progress.Failed);
    }
}
