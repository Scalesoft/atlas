using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Platform.Jobs;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages;

/// <summary>
/// The 3.6 conversion of requirement blocks to requirement headings (model 2026-11-03 §10b, shape 2026-11-04 §5; docs/background-jobs.md
/// „Migrace dat mezi verzemi“): batches of <see cref="ConvertRequirementsHeadingsHandler"/>, each its own transaction holding the
/// checkpoint, while there is work; a stop request ends the run before the next batch and what committed stays. A page's unsaved live
/// edits with no activity in the last <see cref="Commands.ConvertRequirementsOverviewHandler.PassDelay"/> are stored as their author's revision first
/// (<see cref="Collaboration.Commands.CheckpointLivePage"/>), so one run after the upgrade converts every page nobody is typing in. Re-runnable: it converts
/// only what is still in the old shape and skips pages an administrator rolled back. A request with the key <see cref="DryRunKey"/>
/// writes nothing and logs the report (numbers and ids only).
/// Also the move of a version-3 page's party into its headings' attributes (shape §6). Queued by migration 2026110830 where a page still
/// holds a requirement block, by 2026111010 where one still declares version 3, and again by every backup restore.
/// </summary>
public sealed class RequirementsHeadingsJob : IJobDefinition
{
    public const string JobId = "pages.requirements-headings";
    public const string DryRunKey = "dry-run";

    public string Id => JobId;

    public string Title => "Převod požadavků na nadpisy";

    public string Description => "Převede bloky požadavků na nadpisy požadavků verze 3.6 (stav, druh, závislosti, strana, termín a místo uložení v textu sekce) a u stránek verze 3 přesune stranu z řádku údajů do nadpisu; původní obsah uloží pro návrat a historii stránek nechá, jak byla. Zkušební běh (klíč dry-run) nic nezapíše a vypíše jen zprávu.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        var dryRun = context.Key == DryRunKey;
        RequirementsHeadingsRound round;
        do
        {
            // A step reads a stop request at once, the heartbeat only every 30 s; it stores the checkpoint as it is.
            var current = await context.StepAsync((session, token) => ConvertRequirementsHeadingsHandler.ProgressAsync(session, context.RunId, token), ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                // Unsaved live edits nobody is typing now (right after a deploy: nobody) are stored first, as the server's own checkpoint
                // stores them, outside the batch's page locks; the save's read converts the page with them (repair on open), so the next
                // page comes next, else the batch. The same page again: its checkpoint stored nothing (no author may save it), the batch defers it.
                CheckpointLivePage? last = null;
                while (!dryRun && await ConvertRequirementsHeadingsHandler.UnsavedAsync(context.Services.GetRequiredService<ISession>(), current.AfterPageId, ct) is { } unsaved
                       && unsaved != last)
                {
                    context.Log.LogInformation("Stránka {PageId}: neuložené živé úpravy se ukládají před převodem.", unsaved.PageId);
                    await bus.InvokeAsync(unsaved, ct);
                    last = unsaved;
                }
                round = await bus.InvokeAsync<RequirementsHeadingsRound>(new ConvertRequirementsHeadings(context.RunId, dryRun), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One page, one transaction: its failure (a timeout, an error) rolled back that page only; the run goes on past it.
                // The step's value is the run's checkpoint: the progress past the page that failed.
                var skipped = await context.StepAsync((session, token) => ConvertRequirementsHeadingsHandler.SkipAsync(session, context.RunId, current.AfterPageId, token), ct);
                var moved = skipped.AfterPageId != current.AfterPageId;
                context.Log.LogWarning("Stránka {PageId} se nepřevedla ({Error}); běh pokračuje, další běh ji zkusí znovu.", moved ? skipped.AfterPageId : null,
                    error.GetType().Name + ": " + error.Message);
                round = new RequirementsHeadingsRound(moved ? 1 : 0, skipped, []);
            }
            context.Summarize(round.Progress);
            foreach (var report in round.Reports.Where(x => x.Cases.Count > 0 || !x.Passes))
            {
                context.Log.LogInformation("Stránka {PageId}: {Before} požadavků před, {After} po; případy {Cases}; neuznané ztráty {Losses}, zisky {Gains}.",
                    report.PageId, report.Before.Count, report.After.Count, string.Join(", ", report.Cases.GroupBy(x => x.Kind).Select(x => $"{x.Key} {x.Count()}")),
                    report.Losses.Count(x => x.Accepted is null), report.Gains.Count(x => x.Accepted is null));
            }
        }
        while (round.Pages > 0);
        context.Log.LogInformation("Převod skončil{DryRun}: {Pages} stránek, {Requirements} požadavků, převedeno {Revisions} verzí, odloženo {Deferred} stránek, ponecháno {Refused} verzí, selhalo {Failed} stránek, neuznané ztráty {Losses}, zisky {Gains}.",
            dryRun ? " (zkušební běh, nic nezapsáno)" : "", round.Progress.Pages, round.Progress.Requirements, round.Progress.Revisions, round.Progress.Deferred,
            round.Progress.Refused, round.Progress.Failed, round.Progress.Losses, round.Progress.Gains);
    }
}
