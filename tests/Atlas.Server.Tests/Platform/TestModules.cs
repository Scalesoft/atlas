using Atlas.Server.Platform.Modules;
using Microsoft.Extensions.Configuration;

namespace Atlas.Server.Tests.Platform;

/// <summary>Module catalogs for test hosts: every optional module of the distribution on.</summary>
internal static class TestModules
{
    public static IReadOnlyDictionary<string, string?> AllEnabledSettings { get; } =
        AtlasModules.All.ToDictionary(module => $"Modules:{module.Descriptor.Id}:Enabled", _ => (string?)"true");

    public static ModuleCatalog AllEnabled { get; } =
        ModuleCatalog.Build(new ConfigurationBuilder().AddInMemoryCollection(AllEnabledSettings).Build(), AtlasModules.All);
}
