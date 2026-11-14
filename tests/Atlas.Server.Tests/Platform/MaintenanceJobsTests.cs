using Atlas.Server.Platform.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>The fixed-plan jobs of the server (3.3 plan §1): their ids key the Job rows, so they are pinned here with their plans, and each
/// one runs to the end in the calling flow. The jobs of the AI modules and of the vaults have their own tests next to their runners; the
/// OIDC cleanup exists only with an identity provider.</summary>
public sealed class MaintenanceJobsTests
{
    [PostgreSqlFact]
    public async Task The_server_registers_the_maintenance_jobs_with_their_plans_and_each_runs_to_the_end()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_maintenance_jobs_", settings => settings["Vaults:RefreshSeconds"] = "60");
        var jobs = site.Services.GetServices<IJobDefinition>().ToDictionary(x => x.Id);
        var hourly = JobSchedule.Every(TimeSpan.FromHours(1));

        Assert.Equal(hourly, jobs["retention"].Schedule);
        Assert.Equal(hourly, jobs["assistant.maintenance"].Schedule);
        Assert.Equal(hourly, jobs["ai-index.maintenance"].Schedule);
        Assert.Equal(JobSchedule.Every(TimeSpan.FromSeconds(60)), jobs["vaults.refresh"].Schedule);
        Assert.IsType<JobSchedule.CronSchedule>(jobs["trash.cleanup"].Schedule);
        foreach (var id in new[] { "retention", "assistant.maintenance", "ai-index.maintenance", "vaults.refresh", "trash.cleanup" })
        {
            Assert.False(string.IsNullOrWhiteSpace(jobs[id].Title));
            Assert.False(string.IsNullOrWhiteSpace(jobs[id].Description));
            var runId = await site.Services.GetRequiredService<IJobRunner>().RunToEndAsync(id, "test");
            Assert.Equal("succeeded", await site.ScalarAsync("""SELECT "State" FROM "JobRun" WHERE "Id" = @id""", ("id", runId)));
        }
    }
}
