using Atlas.Server.Features.SpaceSettings.Domain;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class SpaceAppearanceTests
{
    [Theory]
    [InlineData("compass", "green", "compass", "green")]
    [InlineData("server", "teal-4", "server", "teal-4")]
    [InlineData("server", "teal-6", "server", "blue")]
    [InlineData("script", "purple", "book-open", "blue")]
    [InlineData(null, null, "book-open", "blue")]
    public void Normalize_falls_back_to_defaults(string? icon, string? color, string expectedIcon, string expectedColor)
    {
        Assert.Equal(new SpaceAppearance(expectedIcon, expectedColor), SpaceAppearance.Normalize(icon, color));
    }

    [Fact]
    public void Validate_rejects_unknown_values_with_400()
    {
        Assert.Equal(new SpaceAppearance("fingerprint", "coral"), SpaceAppearance.Validate("fingerprint", "coral"));
        var error = Assert.Throws<ApiException>(() => SpaceAppearance.Validate("script", "blue"));
        Assert.Equal(400, error.Status);
        Assert.Equal("Vyberte podporovanou ikonu a barvu prostoru.", error.Message);
        Assert.Equal(400, Assert.Throws<ApiException>(() => SpaceAppearance.Validate("book-open", "purple")).Status);
    }

    [Theory]
    [InlineData("server", true)] // core catalog
    [InlineData("rocket", true)] // icon pack
    [InlineData("sparkles", true)] // legacy: retired from the pickers
    [InlineData("archive", true)] // legacy: given by the Wiki.js import
    [InlineData("triangles-centerline-dashed-horizontal", true)] // the longest pack name, 38 characters
    [InlineData("<svg onload=alert(1)>", false)]
    [InlineData("FOLDER", false)]
    [InlineData(" folder", false)]
    [InlineData("house", false)] // a Lucide name outside the pack: the core draws it as "home"
    [InlineData("script", false)]
    public void Validate_accepts_only_core_pack_and_legacy_icons(string icon, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(new SpaceAppearance(icon, "blue"), SpaceAppearance.Validate(icon, "blue"));
        }
        else
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => SpaceAppearance.Validate(icon, "blue")).Status);
        }
    }

    [Fact]
    public void Every_accepted_icon_is_a_name_the_database_stores()
    {
        // The database keeps only the shape and 40 characters; a name it would refuse fails only on save.
        Assert.All(AppearanceCatalog.Icons, icon =>
        {
            Assert.Matches(@"^[a-z0-9]+(-[a-z0-9]+)*\z", icon);
            Assert.InRange(icon.Length, 1, 40);
        });
    }
}
