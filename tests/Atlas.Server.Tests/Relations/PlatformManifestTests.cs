using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using Xunit;

namespace Atlas.Server.Tests.Relations;

/// <summary>
/// tests/fixtures/content/platform-manifest.json: the registries the web mirrors, written from the server's own declarations: every
/// relation kind with the end it uses (<see cref="RelationKinds.UsedEnd"/>) and every object type. The web's
/// src/objects/relationsApi.test.ts compares it with kindInfo, typeInfo and the board's object cards, so a kind or a type added here
/// fails there until the web has its entry. Regenerate after adding one with
/// <c>ATLAS_UPDATE_GOLDEN=1 dotnet test --filter PlatformManifestTests</c>.
/// </summary>
[Trait("Category", "Guard")]
public sealed class PlatformManifestTests
{
    [Fact]
    public async Task The_platform_manifest_lists_every_relation_kind_and_object_type()
    {
        var (source, target) = (Guid.NewGuid(), Guid.NewGuid());
        var kinds = typeof(RelationKinds).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal)
            .Select(kind => (JsonNode)new JsonObject
            {
                ["kind"] = kind, ["used"] = RelationKinds.UsedEnd(new ObjectEdge(source, target, kind)) == source ? "out" : "in",
            });
        // A type's id is a constant, while its constructor wants the module's services: read it from an uninitialized instance.
        var types = typeof(IObjectType).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IObjectType).IsAssignableFrom(type))
            .Select(type => ((IObjectType)RuntimeHelpers.GetUninitializedObject(type)).TypeId).Order(StringComparer.Ordinal)
            .Select(id => (JsonNode)JsonValue.Create(id));
        var manifest = new JsonObject { ["relationKinds"] = new JsonArray([.. kinds]), ["objectTypes"] = new JsonArray([.. types]) };
        var actual = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        var path = Path.Combine(SourceDirectory(), "..", "..", "fixtures", "content", "platform-manifest.json");
        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(path, actual, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), "Missing " + path + "; generate it with ATLAS_UPDATE_GOLDEN=1.");
        Assert.Equal((await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    private static string SourceDirectory([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path)!;
    }
}
