using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>One log line, once, that the token-rights upgrade (migration 2026100600) promoted accounts to level publish and write tokens to
/// draft + publish while a WikiAdmin has not confirmed it: how many of each, in the run's summary too. A <see cref="JobSchedule.Once"/> job
/// (the first start after it exists, until it succeeds), so it no longer asks whether this very start ran the migration. Counts only, never
/// names or token values.</summary>
public sealed class ApiLevelsReviewNotice : IJobDefinition
{
    public string Id => "personal-api.levels-review";

    public string Title => "Oznámení o právech tokenů";

    public string Description => "Jednou zapíše do logu, kolik účtů a tokenů povýšilo přechodné nastavení práv osobního API, dokud to správce nepotvrdí.";

    public JobSchedule? Schedule => JobSchedule.Once;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var review = await context.Services.GetRequiredService<ISession>().QueryOver<ApiLevelsReview>().Where(x => x.ConfirmedAt == null).Take(1).SingleOrDefaultAsync(ct);
        if (review is null)
        {
            return;
        }
        context.Summarize(new { accounts = review.Accounts, tokens = review.Tokens });
        context.Log.LogInformation("Osobní API: {Accounts} účtů převedeno na úroveň Zápis, {Tokens} tokenů s rozsahem zápisu.",
            review.Accounts, review.Tokens);
    }
}
