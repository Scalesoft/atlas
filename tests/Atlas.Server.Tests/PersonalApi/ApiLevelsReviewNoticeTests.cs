using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>The notice of the token-rights upgrade (migration 2026100600) is the job personal-api.levels-review, run once: it reports the
/// review a WikiAdmin has not confirmed in its summary and says nothing when there is none.</summary>
public sealed class ApiLevelsReviewNoticeTests
{
    private const string JobId = "personal-api.levels-review";

    [PostgreSqlFact]
    public async Task The_job_reports_the_unconfirmed_review_and_nothing_when_there_is_none_or_it_is_confirmed()
    {
        // The test host leaves out the personal API module (authentication, OpenAPI): it registers the job as the module does.
        await using var host = await AtlasTestHost.CreateAsync("atlas_api_levels_notice_", services => services.AddJob<ApiLevelsReviewNotice>());
        var runner = host.Services.GetRequiredService<IJobRunner>();
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);

        // A fresh installation holds no review.
        Assert.Equal(("succeeded", ""), await RunAsync(host, runner));

        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "ApiLevelsReview"("Id","Accounts","Tokens","ConfirmedById","ConfirmedAt") VALUES (gen_random_uuid(), 1, 1, '{admin.Id}', now())
            """);
        Assert.Equal(("succeeded", ""), await RunAsync(host, runner));

        await OutboxTests.ExecuteAsync(host.ConnectionString, """INSERT INTO "ApiLevelsReview"("Id","Accounts","Tokens") VALUES (gen_random_uuid(), 3, 5)""");
        Assert.Equal(("succeeded", "3,5"), await RunAsync(host, runner));
    }

    private static async Task<(object? State, object? Summary)> RunAsync(AtlasTestHost host, IJobRunner runner)
    {
        var run = await runner.RunToEndAsync(JobId, "Test");
        return (await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id"={run}"""),
            await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT COALESCE(("Summary"->>'accounts') || ',' || ("Summary"->>'tokens'), '') FROM "JobRun" WHERE "Id"={run}"""));
    }
}
