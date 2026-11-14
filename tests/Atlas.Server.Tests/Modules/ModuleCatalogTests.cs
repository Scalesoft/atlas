using System.Reflection;
using Atlas.Server.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Modules;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void Mandatory_modules_are_on_and_optional_ones_follow_their_switch_defaulting_to_off()
    {
        var modules = new IAtlasModule[] { Module("pages", mandatory: true, types: ["atlas.page"]), Module("boards", requires: ["pages"], types: ["atlas.board"]) };

        var off = ModuleCatalog.Build(Settings(), modules);
        Assert.Equal(["pages"], off.Enabled.Select(m => m.Descriptor.Id));
        Assert.Equal(["boards"], off.Disabled.Select(m => m.Descriptor.Id));
        Assert.False(off.IsEnabled("boards"));

        // Configuration keys are case-insensitive, so the documented Modules:Boards:Enabled reaches the module "boards".
        var on = ModuleCatalog.Build(Settings(("Modules:Boards:Enabled", "true")), modules);
        Assert.Equal(["pages", "boards"], on.Enabled.Select(m => m.Descriptor.Id));
        Assert.Empty(on.Disabled);
        AssertDescribes(on, "pages:1,boards:1", "atlas.page,atlas.board");
        AssertDescribes(off, "pages:1", "atlas.page");
    }

    [Fact]
    public void Startup_fails_with_a_message_naming_the_module_for_every_inconsistent_distribution()
    {
        var pages = Module("pages", mandatory: true, types: ["atlas.page"]);
        AssertRefused("'pages' is declared twice", Settings(), pages, Module("pages", types: ["atlas.other"]));
        AssertRefused("'atlas.page' is declared by both 'pages' and 'copy'", Settings(), pages, Module("copy", types: ["atlas.page"]));
        AssertRefused("'pages' is mandatory", Settings(("Modules:pages:Enabled", "false")), pages);
        AssertRefused("'boards' requires 'missing'", Settings(("Modules:boards:Enabled", "true")), pages, Module("boards", requires: ["missing"]));
        AssertRefused("'boards' requires 'extra', which is disabled", Settings(("Modules:boards:Enabled", "true")),
            pages, Module("extra"), Module("boards", requires: ["extra"]));
        // A disabled module may depend on anything: only what runs needs its dependencies.
        Assert.Equal(["pages"], ModuleCatalog.Build(Settings(), [pages, Module("boards", requires: ["missing"])]).Enabled.Select(m => m.Descriptor.Id));
    }

    [Fact]
    public void The_distribution_declares_pages_as_mandatory_and_boards_as_an_optional_module_over_pages()
    {
        var catalog = ModuleCatalog.Build(Settings(("Modules:Boards:Enabled", "true")), AtlasModules.All);
        AssertDescribes(catalog, "pages:1,boards:1", "atlas.page,atlas.board");
        var boards = Assert.Single(AtlasModules.All, m => m.Descriptor.Id == "boards");
        Assert.Equal(["pages"], boards.Descriptor.Requires);
        Assert.False(boards.Mandatory);
        Assert.True(Assert.Single(AtlasModules.All, m => m.Descriptor.Id == "pages").Mandatory);
    }

    private static void AssertDescribes(ModuleCatalog catalog, string modules, string contentTypes)
    {
        var list = catalog.Describe();
        Assert.Equal(modules, string.Join(",", list.Modules.Select(m => m.Id + ":" + m.Version)));
        Assert.Equal(contentTypes, string.Join(",", list.ContentTypes));
    }

    private static void AssertRefused(string expected, IConfiguration configuration, params IAtlasModule[] modules)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Build(configuration, modules));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Settings(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();
    }

    private static FakeModule Module(string id, bool mandatory = false, string[]? requires = null, string[]? types = null)
    {
        return new FakeModule(new ModuleDescriptor(id, 1, requires ?? [], types ?? []), mandatory);
    }

    private sealed class FakeModule(ModuleDescriptor descriptor, bool mandatory) : IAtlasModule
    {
        public ModuleDescriptor Descriptor => descriptor;
        public bool Mandatory => mandatory;
        public Assembly MigrationsAssembly => typeof(FakeModule).Assembly;
        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }
        public void AddPurge(IServiceCollection services)
        {
        }
        public void ConfigureWolverine(WolverineOptions options)
        {
        }
        public void MapEndpoints(IEndpointRouteBuilder app)
        {
        }
    }
}
