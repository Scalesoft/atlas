using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// docker-compose.yml pins the database image (CI and the Podman tests read it from there); Aspire repeats it in code. Both run the
/// same digest with the same builtin collation, so local development sorts and migrates as production does.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class DatabaseImagePinTests
{
    [Fact]
    public void Aspire_runs_the_database_image_and_collation_docker_compose_pins()
    {
        var root = RepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var appHost = File.ReadAllText(Path.Combine(root, "src", "Atlas.AppHost", "Program.cs"));
        var pin = ComposePin().Match(compose);
        Assert.True(pin.Success, "docker-compose.yml pins no ATLAS_POSTGRES_IMAGE default");
        Assert.Contains($".WithImage(\"{pin.Groups["name"].Value}\", \"{pin.Groups["tag"].Value}\")", appHost);
        Assert.Contains($".WithImageSHA256(\"{pin.Groups["digest"].Value}\")", appHost);
        var initdb = InitDbArgs().Match(compose);
        Assert.True(initdb.Success, "docker-compose.yml sets no POSTGRES_INITDB_ARGS");
        Assert.Contains($".WithEnvironment(\"POSTGRES_INITDB_ARGS\", \"{initdb.Groups[1].Value.Trim()}\")", appHost);
    }

    [GeneratedRegex(@"ATLAS_POSTGRES_IMAGE:-docker\.io/(?<name>[^:}]+):(?<tag>[^@}]+)@sha256:(?<digest>[a-f0-9]{64})\}")]
    private static partial Regex ComposePin();

    [GeneratedRegex(@"POSTGRES_INITDB_ARGS:\s*(.+)")]
    private static partial Regex InitDbArgs();

    private static string RepositoryRoot([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));
}
