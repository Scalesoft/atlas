using Atlas.Server.Features.ContentExport;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class ContentExportNamesTests
{
    [Theory]
    [InlineData("Acme Care: příručka", "Acme Care - příručka")]
    [InlineData("  Návod: A/B \u0007 krok?  ", "Návod - A-B krok")]
    [InlineData("Verze 2.1.", "Verze 2.1")]
    [InlineData("Řekl \"ano\" <hned>", "Řekl 'ano' hned")]
    [InlineData("...", "stranka")]
    [InlineData("CON", "stranka")]
    [InlineData("com1.txt", "stranka")]
    [InlineData("", "stranka")]
    public void Names_files_after_the_title_portable_on_every_file_system(string title, string expected)
        => Assert.Equal(expected, ContentExportNames.Stem(title, "stranka"));

    [Fact]
    public void A_long_title_is_cut_without_splitting_a_character()
    {
        Assert.Equal(ContentExportNames.MaximumStemLength, ContentExportNames.Stem(new string('x', 150), "stranka").Length);
        var emoji = new string('x', ContentExportNames.MaximumStemLength - 1) + "😀";
        Assert.Equal(new string('x', ContentExportNames.MaximumStemLength - 1), ContentExportNames.Stem(emoji, "stranka"));
    }

    [Fact]
    public void Cover_details_keep_the_canonical_order_and_refuse_unknown_names()
    {
        Assert.Equal(PdfCoverDetails.All, PdfCoverDetails.Parse(null));
        Assert.Empty(PdfCoverDetails.Parse(""));
        // A JSON list with null in it (an API client's bug) is no detail, not a server error.
        Assert.Equal(["revision"], PdfCoverDetails.Normalize(["revision", null!]));
        Assert.Equal(["revision", "pageId"], PdfCoverDetails.Parse("pageId, revision,revision"));
        Assert.Equal(400, Assert.Throws<ApiException>(() => PdfCoverDetails.Parse("revision,secret")).Status);
    }
}
