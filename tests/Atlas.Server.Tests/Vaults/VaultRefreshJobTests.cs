using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>The vault refresh as the job "vaults.refresh": it exists only with a refresh interval (0 turns it off), and a run is one
/// pass of <c>VaultRefreshService.RefreshAllAsync</c>.</summary>
public sealed class VaultRefreshJobTests
{
    [PostgreSqlFact]
    public async Task Without_an_interval_there_is_no_job()
    {
        await using var f = await VaultFixture.CreateAsync();

        Assert.DoesNotContain(f.Host.Services.GetServices<IJobDefinition>(), x => x.Id == "vaults.refresh");
    }

    [PostgreSqlFact]
    public async Task The_job_runs_every_interval_and_one_run_is_a_refresh_pass()
    {
        await using var f = await VaultFixture.CreateAsync(settings: new Dictionary<string, string?> { ["Vaults:RefreshSeconds"] = "30" });
        var job = f.Host.Services.GetServices<IJobDefinition>().Single(x => x.Id == "vaults.refresh");
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "tym.kdbx", "argon2id-chacha20-none.kdbx");

        // Someone changes the master key in KeePass and saves: the pass finds the stored password no longer opens the file.
        f.PutFile("tym.kdbx", "changed-key.kdbx");
        var runId = await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(job.Id, "test");

        Assert.Equal(JobSchedule.Every(TimeSpan.FromSeconds(30)), job.Schedule);
        Assert.Equal(JobRetry.None, job.Retry);
        Assert.Equal("succeeded", await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}"""));
        Assert.Equal("needsPassword", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults())).Status);
        Assert.False(f.Keyring.Contains(vault.Id));
    }
}
