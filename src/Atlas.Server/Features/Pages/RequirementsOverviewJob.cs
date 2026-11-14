using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.Pages;

/// <summary>
/// The 3.4 conversion of stored page content to today's shape (plan R6, docs/background-jobs.md „Migrace dat mezi verzemi“): migration
/// 2026110480 (and 2026110550 for the history lines of 3.5.1) requests one run where a page declares <c>atlas-requirements</c>, 2026110995
/// where a revision holds a disclosure (3.6), as does every restore of a backup
/// (InstallationModules.PagesRebuild). Batches of <see cref="ConvertRequirementsOverviewHandler"/>, each its own transaction holding the
/// checkpoint, while there is work; a stop request ends the run before the next batch and what committed stays. It rebuilds from the
/// current state, so another run (Úlohy „Spustit teď“, atlas_run_job) changes only what is still in the old shape.
/// </summary>
public sealed class RequirementsOverviewJob : IJobDefinition
{
    public const string JobId = "pages.requirements-overview";

    public string Id => JobId;

    public string Title => "Převod souhrnu nasazení na přehled";

    public string Description => "Převede uložený souhrn nasazení ze stránek verze 3.3 na přehled požadavků verze 3.4 a záznamy historie požadavků rozpadlé na vnořené číslované seznamy složí zpět na řádek s datem (3.5.1); rozbalovací blok převede na nadpis s obsahem (3.6); historie stránek zůstává, jak byla.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        RequirementsOverviewRound round;
        do
        {
            // A step reads a stop request at once, the heartbeat only every 30 s; it stores the checkpoint as it is.
            await context.StepAsync((session, token) => ConvertRequirementsOverviewHandler.ProgressAsync(session, context.RunId, token), ct);
            ct.ThrowIfCancellationRequested();
            round = await bus.InvokeAsync<RequirementsOverviewRound>(new ConvertRequirementsOverview(context.RunId), ct);
            context.Summarize(round.Progress);
        }
        while (round.Pages > 0);
        context.Log.LogInformation("Převod skončil: {Pages} stránek, převedeno {Revisions} verzí, odloženo {Deferred} stránek, ponecháno {Refused} verzí.",
            round.Progress.Pages, round.Progress.Revisions, round.Progress.Deferred, round.Progress.Refused);
    }
}
