using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Platform.Jobs;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector;

/// <summary>The checkpoint of a run: pages refreshed up to <see cref="AfterPageId"/> and how many; <see cref="Batch"/> the last batch's size.</summary>
public sealed record ProjectorIndexProgress(Guid AfterPageId, int Pages, int Batch);

/// <summary>
/// The full rebuild of the requirement index from every page's working and published revision (<see cref="ProjectorIndex.RefreshAsync"/>),
/// in batches of <see cref="BatchPages"/> pages by id, each its own step holding the checkpoint: migration 2026110870 requests it when it drops
/// the index triggers, every restore of a backup again (InstallationModules), and an administrator may run it from Úlohy. It rebuilds from
/// the current state, so a re-run repairs whatever drifted and continues from its checkpoint after a stop.
/// </summary>
public sealed class ProjectorIndexJob : IJobDefinition
{
    public const string JobId = "projector.requirements-index";
    public const int BatchPages = 50;

    public string Id => JobId;

    public string Title => "Přestavba indexu požadavků";

    public string Description => "Znovu sestaví index požadavků (přehledy, tabule, závislosti mezi stránkami) z pracovních a publikovaných verzí všech stránek.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var progress = context.Progress<ProjectorIndexProgress>() ?? new ProjectorIndexProgress(Guid.Empty, 0, 0);
        do
        {
            var after = progress.AfterPageId;
            var done = progress.Pages;
            progress = await context.StepAsync(async (session, token) =>
            {
                var pages = await NextPagesAsync(session, after, token);
                await ProjectorIndex.RefreshAsync(session, pages, token);
                return new ProjectorIndexProgress(pages.Count > 0 ? pages[^1] : after, done + pages.Count, pages.Count);
            }, ct);
            context.Summarize(progress);
        }
        while (progress.Batch > 0);
        context.Log.LogInformation("Index požadavků přestavěn: {Pages} stránek.", progress.Pages);
    }

    /// <summary>The next pages by id that are not purged.</summary>
    private static async Task<IReadOnlyList<Guid>> NextPagesAsync(ISession session, Guid after, CancellationToken ct)
        => (await session.CreateSQLQuery("""SELECT "Id" FROM "Page" WHERE "PurgedAt" IS NULL AND "Id" > :after ORDER BY "Id" LIMIT :pages""")
            .AddScalar("Id", NHibernateUtil.Guid).SetParameter("after", after).SetParameter("pages", BatchPages).ListAsync<Guid>(ct)).ToList();
}
