using Atlas.Server.Tests.Search;
using JasperFx.CodeGeneration;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class SharedHandlerCompilerTests
{
    // The load must stay file-backed: in-memory loads from a Dev Drive testhost can block for seconds per handler.
    [Fact]
    public async Task Concurrent_compiles_reuse_file_backed_executable_handlers()
    {
        var assemblies = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            var generated = new GeneratedAssembly(new GenerationRules("CompilerRegression"));
            var name = "Handler" + index % 2;
            generated.AddType(name, typeof(CompilerProbe));
            new SharedHandlerCompiler().Compile(generated);
            var assembly = Assert.IsAssignableFrom<System.Reflection.Assembly>(generated.Assembly);
            var handler = Assert.IsAssignableFrom<CompilerProbe>(Activator.CreateInstance(assembly.GetType("CompilerRegression." + name)!));
            Assert.Equal(42, handler.Value);
            Assert.False(string.IsNullOrEmpty(assembly.Location));
            Assert.True(File.Exists(assembly.Location));
            return assembly;
        })));

        Assert.Same(assemblies[0], assemblies[2]);
        Assert.Same(assemblies[1], assemblies[3]);
        Assert.NotSame(assemblies[0], assemblies[1]);
    }

    // The same compiled search handler must resolve the invoking host's database and scoped actor every time.
    [PostgreSqlFact]
    public async Task Shared_handler_code_keeps_databases_and_concurrent_actors_isolated()
    {
        await using var first = await SearchFixture.CreateAsync();
        // Own host and database: the subject is one compiled handler serving two hosts on two databases.
        await using var second = await SearchFixture.CreateAsync(isolated: true);
        var firstPage = await first.Create("Private alpha document");
        var secondPage = await second.Create("Private beta document");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            var fixture = index % 2 == 0 ? first : second;
            var expectedPage = index % 2 == 0 ? firstPage : secondPage;
            var canReadDraft = index % 4 < 2;
            var result = await fixture.Find(canReadDraft ? fixture.Writer : fixture.Reader, "document");
            if (canReadDraft)
            {
                Assert.Equal(expectedPage.Id, Assert.Single(result.Items).Id);
            }
            else
            {
                Assert.Empty(result.Items);
            }
        }));
    }
}

public class CompilerProbe
{
    public int Value => 42;
}
