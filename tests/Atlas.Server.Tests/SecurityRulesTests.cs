using Atlas.Server;
using Xunit;
namespace Atlas.Server.Tests;
public class SecurityRulesTests
{
    [Theory][InlineData(3, 2)][InlineData(2, 3)]
    public void Stale_or_future_versions_conflict(int current, int expected) => Assert.Equal(409, Assert.Throws<ApiException>(() => Rules.ExpectVersion(current, expected)).Status);
    [Fact] public void Matching_version_is_accepted() => Rules.ExpectVersion(4, 4);
    [Theory][InlineData("floating", "blue", "system", "system")][InlineData("connected", "purple", "dark", "system")][InlineData("islands", "green", "auto", "system")][InlineData("islands", "green", "dark", "off")]
    public void Appearance_rejects_unrecognized_values(string layout, string accent, string mode, string motion) => Assert.Throws<ApiException>(() => Rules.ValidateAppearance(new(layout, accent, mode, motion)));
    [Theory]
    [InlineData("blue")][InlineData("green")][InlineData("red")][InlineData("ocean")][InlineData("sunset")][InlineData("indigo")][InlineData("violet")][InlineData("amber")]
    public void Color_styles_work_with_each_layout_mode_and_motion(string accent)
    {
        foreach (var layout in new[] { "connected", "islands" })
        foreach (var mode in new[] { "system", "light", "dark" })
        foreach (var motion in new[] { "system", "reduce" })
            Rules.ValidateAppearance(new(layout, accent, mode, motion));
    }
    // Atlas 2.5: new accounts start on the light ocean accent and follow the device's motion setting.
    [Fact] public void Appearance_defaults_match_decision() => Assert.Equal(new Appearance("connected", "ocean", "system", "system"), Appearance.Default);
    [Theory][InlineData("{}")] [InlineData("{\"format\":\"unknown\",\"version\":1,\"document\":{}}")]
    public void Arbitrary_json_is_not_an_editor_document(string json) => Assert.Throws<ApiException>(() => Rules.ValidateDocument(json, ""));
    [Theory]
    [InlineData("{\"format\":1,\"version\":1,\"document\":{\"type\":\"doc\"}}")]
    [InlineData("{\"format\":\"atlas-rich\",\"version\":\"1\",\"document\":{\"type\":\"doc\"}}")]
    [InlineData("{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":5}}")]
    public void Wrong_json_value_types_produce_a_validation_error(string json) => Assert.Equal(400, Assert.Throws<ApiException>(() => Rules.ValidateDocument(json, "")).Status);
    // The editor refuses to open unknown envelope fields and plugins it does not install (the editor package's validator), so a write does too.
    [Fact] public void Rich_document_with_unknown_plugin_data_is_refused() => Assert.Equal("invalid_document", Assert.Throws<ApiException>(() => Rules.ValidateDocument("""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]},"plugins":[{"id":"custom","version":3}],"future":{"opaque":true}}""", "")).Code);
    [Fact] public void Invalid_document_json_rejected() => Assert.Throws<ApiException>(() => Rules.ValidateDocument("{broken", ""));
    [Theory][InlineData("Řádek\nDruhý")][InlineData("Tab\tulátor")][InlineData("Konec\u0007")][InlineData("faktura‮fdp.exe")][InlineData("A⁦B")]
    public void Names_and_titles_are_one_line_without_control_or_bidi_characters_unless_stored_so(string title)
    {
        Assert.Equal("Pole Název stránky smí mít jen jeden řádek bez řídicích znaků.", Assert.Throws<ApiException>(() => Rules.Required(title, "Název stránky")).Message);
        Assert.Equal(title, Rules.Required(title, "Název stránky", unchanged: title));
        Assert.Throws<ApiException>(() => Rules.Required(title + " nový", "Název stránky", unchanged: title));
    }

    [Fact]
    public void Texts_keep_line_breaks_but_no_other_control_or_bidi_characters_and_synced_lines_are_cleaned()
    {
        Assert.Equal("Řádek\r\nDruhý", Rules.OptionalText(" Řádek\r\nDruhý ", "Popis", 100));
        Assert.Throws<ApiException>(() => Rules.OptionalText("Tab\tulátor", "Popis", 100));
        Assert.Throws<ApiException>(() => Rules.OptionalText("A‮B", "Popis", 100));
        Assert.Equal("Jan Novák admin", Rules.CleanLine(" Jan‮\tNovák\nadmin "));
    }

    [Fact]
    public void Largest_OpenAPI_page_fits_and_each_size_limit_fails_with_a_400_that_names_it()
    {
        Rules.ValidateDocument(OpenApiDocuments.Rich(), OpenApiDocuments.Markdown);
        Rules.ValidateDocument(OpenApiDocuments.RichOfLength(2_000_000), OpenApiDocuments.Markdown);
        var json = Assert.Throws<ApiException>(() => Rules.ValidateDocument(OpenApiDocuments.RichOfLength(2_000_001), OpenApiDocuments.Markdown));
        Assert.Equal((400, "document_too_large"), (json.Status, json.Code));
        // Rounded up: one character over the limit never reads as the limit itself.
        Assert.Equal("Stránka je příliš velká: uložený obsah má 2,1 MB, limit je 2 MB. Rozdělte velké tabulky do více stránek.", json.Message);
        var markdown = Assert.Throws<ApiException>(() => Rules.ValidateDocument(OpenApiDocuments.Rich(), new string('a', 1_250_000)));
        Assert.Equal("Stránka je příliš velká: text stránky má 1,3 MB, limit je 1 MB. Rozdělte obsah do více stránek.", markdown.Message);
    }
}
