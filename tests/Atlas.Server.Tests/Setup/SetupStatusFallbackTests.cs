using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Platform.Hosting;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Setup;

public sealed class SetupStatusFallbackTests
{
    /// <summary>
    /// The wizard must render while the database is unreachable, so /api/setup/status catches
    /// ApiException 503 and answers 200. That only works if the 503 the scoped session throws
    /// survives Wolverine unwrapped, which is what this asserts; the endpoint's catch clause matches
    /// on Status alone. The HTTP level of the same case is the fact below; OidcRuntimeLoader no longer
    /// fails the boot when the database is unreachable.
    /// </summary>
    [PostgreSqlFact]
    public async Task An_unreachable_database_surfaces_as_an_unwrapped_503_from_the_workspace_query()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_setup_unavailable_", services =>
        {
            var unreachable = new NpgsqlConnectionStringBuilder(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION"))
            {
                Port = 1,
                Timeout = 1
            }.ConnectionString;
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:atlas-db"] = unreachable })
                .Build());
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<WorkspaceState>(new GetWorkspaceState()));
        Assert.Equal(503, error.Status);
        Assert.Equal("Databáze není dostupná. Zkontrolujte připojení a serverové logy.", error.Message);
    }

    /// <summary>
    /// The wizard, liveness and readiness must answer while the database is unreachable: the host boots
    /// (OidcRuntimeLoader is fail soft), /api/setup/status answers 200 with the unavailable shape,
    /// /health/live stays 200 and /health/ready reports 503 because AtlasDatabase is not initialized.
    /// </summary>
    [PostgreSqlFact]
    public async Task An_unreachable_database_still_serves_setup_status_liveness_and_a_503_readiness()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_setup_http_unavailable_", settings =>
            settings["ConnectionStrings:atlas-db"] = "Host=127.0.0.1;Port=1;Database=atlas;Username=postgres;Timeout=1");
        using var client = site.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        var status = await AtlasHttpSite.Json(await client.GetAsync("/api/setup/status"), HttpStatusCode.OK);
        Assert.False(status["databaseReady"]!.GetValue<bool>());
        Assert.Equal(AtlasVersion.Current, status["version"]!.GetValue<string>());
        Assert.True(status["requiresSetup"]!.GetValue<bool>());
        Assert.Equal("Databáze není dostupná. Zkontrolujte připojení a serverové logy.", status["error"]!.GetValue<string>());
    }
}
