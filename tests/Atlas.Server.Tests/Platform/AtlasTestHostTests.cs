using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Recovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class AtlasTestHostTests
{
    [PostgreSqlFact]
    public async Task Host_owns_one_in_memory_configuration_without_file_watchers()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_host_config_");
        var configurations = host.Services.GetServices<IConfiguration>().Cast<IConfigurationRoot>().ToArray();

        Assert.DoesNotContain(configurations.SelectMany(configuration => configuration.Providers),
            provider => provider is FileConfigurationProvider);
        var configuration = Assert.Single(configurations);
        Assert.Equal(host.ConnectionString, configuration.GetConnectionString("atlas-db"));
    }

    [PostgreSqlFact]
    public async Task Hosts_isolate_and_clean_up_server_token_files()
    {
        string firstDirectory;
        string secondDirectory;
        await using (var first = await AtlasTestHost.CreateAsync("atlas_host_files_", services => services.AddAuth()))
        await using (var second = await AtlasTestHost.CreateAsync("atlas_host_files_", services => services.AddAuth()))
        {
            var firstFiles = first.Services.GetRequiredService<ServerTokenFiles>();
            var secondFiles = second.Services.GetRequiredService<ServerTokenFiles>();
            firstDirectory = firstFiles.DirectoryPath;
            secondDirectory = secondFiles.DirectoryPath;
            Assert.NotEqual(firstDirectory, secondDirectory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), firstDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), secondDirectory, StringComparison.OrdinalIgnoreCase);

            var firstToken = await firstFiles.BootstrapAsync(null, default);
            var secondToken = await secondFiles.BootstrapAsync(null, default);
            Assert.NotEqual(firstToken, secondToken);
            Assert.True(File.Exists(firstFiles.BootstrapPath));
            Assert.True(File.Exists(secondFiles.BootstrapPath));
        }
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
    }
}
