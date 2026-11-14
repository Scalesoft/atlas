using System.Text.Json;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>The parts of MCP and personal API consistency that need no site: argument typing, the publication names, file types and the API wording of a conflict.</summary>
public sealed class McpApiConsistencyTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"link":{"type":["string","null"]},"contact":{"type":"object"},"count":{"type":"integer"}}}
        """).RootElement;

    [Theory]
    [InlineData("""{"link":true}""", "link", "řetězec")]
    [InlineData("""{"contact":"Jana"}""", "contact", "objekt")]
    [InlineData("""{"count":1.5}""", "count", "celé číslo")]
    public void A_wrongly_typed_argument_is_named_with_its_expected_type(string arguments, string name, string expected)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!;
        var error = new JsonException("x");
        Assert.True(McpArguments.IsBindingFailure(new InvalidOperationException("wrapped", error)));
        var message = McpArguments.BindingMessage(error, Schema, values, "atlas_test");
        Assert.Contains($"Argument {name} má špatný typ", message);
        Assert.Contains(expected, message);
    }

    [Theory]
    [InlineData("Manual", PublicationMode.Manual)]
    [InlineData("live", PublicationMode.Live)]
    [InlineData("Živě", PublicationMode.Live)]
    [InlineData("zive", PublicationMode.Live)]
    [InlineData("Ručně", PublicationMode.Manual)]
    [InlineData("automaticky", PublicationMode.Automatic)]
    public void Publication_modes_ignore_case_and_accept_the_czech_names(string text, PublicationMode expected)
    {
        Assert.Equal(expected, PublicationModes.Parse(text));
        Assert.Null(PublicationModes.ParseOrInherit("Inherit"));
        Assert.Equal("invalid_publication_mode", Assert.Throws<ApiException>(() => PublicationModes.Parse("často")).Code);
    }

    [Fact]
    public void A_file_is_typed_by_its_extension_and_an_unknown_one_stays_opaque()
    {
        Assert.Equal("text/plain", AttachmentFiles.MimeTypeOf("poznamky.txt"));
        Assert.Equal("application/octet-stream", AttachmentFiles.MimeTypeOf("data.unknownext"));
    }

    [Fact]
    public void A_version_conflict_is_worded_for_an_api_client_without_an_editor()
    {
        Assert.DoesNotContain("editoru", Rules.ForApiClient(Rules.VersionConflictMessage));
        Assert.Equal("jiná zpráva", Rules.ForApiClient("jiná zpráva"));
    }
}
