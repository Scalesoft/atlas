using System.Reflection;

namespace Atlas.Markdown.Sync.Tests;

public sealed class PackageIdentityTests
{
    [Fact]
    public void Assembly_exposes_the_central_package_identity_and_registration_api()
    {
        var assembly = Assembly.Load("Atlas.Markdown.Sync");

        Assert.Equal("Atlas.Markdown.Sync", assembly.GetName().Name);
        Assert.NotNull(assembly.GetType("Atlas.Markdown.Sync.MarkdownSyncHub"));

        var registrations = assembly.GetType("Atlas.Markdown.Sync.ServiceCollectionExtensions");
        Assert.NotNull(registrations);
        Assert.Contains(registrations.GetMethods(), method => method.Name == "AddAtlasMarkdownSync");
        Assert.Contains(registrations.GetMethods(), method => method.Name == "MapAtlasMarkdownSyncHub");
    }
}
