using System.Text.Json;
using Atlas.Server.Features.ContentExport;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class PdfImagePlanTests
{
    [Fact]
    public void Printed_size_follows_the_presentation_the_renderer_applies()
    {
        var plan = Plan("""
            {"type":"doc","content":[
              {"type":"image","attrs":{"src":"/api/attachments/tall/content"}},
              {"type":"image","attrs":{"src":"/api/attachments/crop/content","presentation":{"width":50,"cropLeft":25,"cropRight":25,"naturalWidth":1920,"naturalHeight":1080}}},
              {"type":"image","attrs":{"src":"/api/attachments/small/content","presentation":{"width":25}}},
              {"type":"columns","attrs":{"photo":"/api/attachments/small/content"}}]}
            """);
        // A scrolling screenshot prints at most 220 mm high: 2598 px at 300 DPI, whatever its width.
        Assert.Equal((554, 2598), PdfImagePlan.Size(1920, 9000, plan.Scale("/api/attachments/tall/content", 1920, 9000)));
        // A crop enlarges what remains: half the column showing half the source keeps every source pixel.
        Assert.Equal(1, plan.Scale("/api/attachments/crop/content", 1920, 1080));
        // The largest use wins: another module shows the same image across the whole column.
        Assert.Equal((1961, 1103), PdfImagePlan.Size(2560, 1440, plan.Scale("/api/attachments/small/content", 2560, 1440)));
    }

    [Fact]
    public void Resolution_drops_only_as_far_as_the_images_need_down_to_150_dpi_and_then_the_export_is_refused_with_the_numbers()
    {
        Assert.Equal(300, Fitted(10).Dpi);
        Assert.Equal(225, Fitted(30).Dpi);
        Assert.Equal(150, Fitted(60).Dpi);
        // 80 full-width screenshots need 43,2 million pixels even at 150 DPI: refused from the headers, before any browser starts.
        var refusal = Assert.Throws<ApiException>(() => Fitted(80));
        Assert.Equal(413, refusal.Status);
        Assert.Equal("pdf_images_too_large", refusal.Code);
        Assert.Contains("150 DPI", refusal.Message);
        Assert.Contains("43,2 mil. pixelů, limit PDF je 40 mil. (80 obrázků, originály 165,9 mil. pixelů)", refusal.Message);
    }

    private static PdfImagePlan Fitted(int screenshots)
    {
        var nodes = Enumerable.Range(0, screenshots).Select(i => "{\"type\":\"image\",\"attrs\":{\"src\":\"/api/attachments/" + i + "/content\"}}");
        var plan = Plan("{\"type\":\"doc\",\"content\":[" + string.Join(',', nodes) + "]}");
        plan.Fit(Enumerable.Range(0, screenshots).ToDictionary(i => $"/api/attachments/{i}/content", _ => (1920, 1080)), 0);
        return plan;
    }

    private static PdfImagePlan Plan(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PdfImagePlan.Read(document.RootElement, source => source.StartsWith("/api/attachments/", StringComparison.Ordinal) ? source : null);
    }
}
