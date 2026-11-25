using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

/// <summary>Real prints run alone after the parallel tests, as the timed tests do: the renderer's wall-clock waits (15 s for the
/// layout, the 100 s deadline) are the product's, and a browser starved by the parallel tests and their database ran out of them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChromiumPrintCollection
{
    public const string Name = "Chromium print";
}

[Collection(ChromiumPrintCollection.Name)]
public sealed class ContentPdfImageScalingTests
{
    // print.css prints 166 mm wide: 1307 px is 200 DPI, the resolution this manual still fits at (150 DPI is the floor for longer ones).
    private const int FullWidthFloor = 1307;

    /// <summary>
    /// A long manual: 36 screenshots (full HD and 1440p), most full width, some at half width. Their originals hold about
    /// 85 million pixels, more than the 40 million the renderer may decode, so this export used to fail with 413. Now each
    /// image reaches the renderer at the size it occupies on paper, here not below 200 DPI, and the PDF is produced.
    /// </summary>
    [ChromiumFact]
    public async Task Long_manual_with_many_screenshots_prints_downscaled_to_the_page_but_not_below_readable_resolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-scaling", Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(web, "assets"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(web, "print.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>Tisk</title></head><body><main id=\"app\"></main><script src=\"/assets/print.js\"></script></body></html>");
            await File.WriteAllTextAsync(Path.Combine(web, "assets", "print.js"), PrintScript);
            var screenshots = new List<(int Width, int Height, int Percent)>();
            for (var i = 0; i < 36; i++) screenshots.Add(i % 9 == 4 ? (2560, 1440, 100) : i % 9 == 8 ? (1920, 1080, 50) : (1920, 1080, 100));
            var images = await ScreenshotsAsync(screenshots);
            Assert.True(images.Sum(x => (long)x.Width * x.Height) > 80_000_000);

            var environment = new AttachmentTests.EnvironmentStub(root);
            using var renderer = new ContentPdfRenderer(environment, new ContentExportFiles(new ConfigurationBuilder().Build(), environment), NullLogger<ContentPdfRenderer>.Instance);
            var archive = Archive(images, screenshots);
            var pdf = await renderer.RenderAsync(archive, "https://wiki.invalid/", PdfCoverDetails.All, CancellationToken.None);
            await using var output = pdf.Stream;
            using var buffer = new MemoryStream();
            await output.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

            var embedded = EmbeddedImages(bytes);
            var full = embedded.Where(x => x.Width >= FullWidthFloor).ToList();
            var half = embedded.Where(x => x.Width < FullWidthFloor).ToList();
            Assert.Equal(screenshots.Count(x => x.Percent == 100), full.Count);
            Assert.Equal(screenshots.Count(x => x.Percent == 50), half.Count);
            // Downscaled to the printed size, here never to less than 200 DPI of the width each one occupies.
            Assert.All(full, x => Assert.InRange(x.Width, FullWidthFloor, 1919));
            Assert.All(half, x => Assert.InRange(x.Width, FullWidthFloor / 2, 1919 / 2));
            Assert.True(embedded.Sum(x => (long)x.Width * x.Height) <= 40_000_000);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static ContentExportArchive Archive(IReadOnlyList<(byte[] Bytes, int Width, int Height)> images, IReadOnlyList<(int Width, int Height, int Percent)> layout)
    {
        var pageId = Guid.NewGuid();
        var attachments = new List<ExportAttachment>();
        var content = new List<object>();
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i < images.Count; i++)
            {
                var id = Guid.NewGuid();
                var path = $"attachments/{id:D}/blob";
                using (var entry = zip.CreateEntry(path, CompressionLevel.NoCompression).Open()) entry.Write(images[i].Bytes);
                attachments.Add(new ExportAttachment(id, pageId, $"obrazovka-{i + 1}.png", "image/png", images[i].Bytes.Length, "", $"/api/attachments/{id:D}/content", path));
                content.Add(new { type = "paragraph", content = new[] { new { type = "text", text = $"Krok {i + 1}: zkontrolujte údaje na obrazovce." } } });
                content.Add(new { type = "image", attrs = new { src = $"/api/attachments/{id:D}/content", alt = $"Obrazovka {i + 1}", presentation = new { width = layout[i].Percent, caption = $"Obrazovka {i + 1}" } } });
            }
            var json = JsonSerializer.Serialize(new { format = "atlas-rich", version = 1, document = new { type = "doc", content } });
            using (var writer = new StreamWriter(zip.CreateEntry($"pages/{pageId:D}/content.json").Open())) writer.Write(json);
            var page = new ExportPage(pageId, Guid.NewGuid(), null, 0, $"/#/pages/{pageId:D}", Guid.NewGuid(), 3, "Příručka s obrazovkami", DateTime.UtcNow, null, true, $"pages/{pageId:D}/content.json", $"pages/{pageId:D}/content.md");
            var manifest = new { exportedAt = DateTime.UtcNow, space = new { name = "Příručky" }, pages = new[] { page }, attachments };
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        stream.Position = 0;
        return new ContentExportArchive(stream, "page.zip", "hash", Guid.NewGuid(), []);
    }

    /// <summary>Real screenshots of a dense application screen: 13 px interface text, the size a manual must keep legible.</summary>
    private static async Task<List<(byte[] Bytes, int Width, int Height)>> ScreenshotsAsync(IEnumerable<(int Width, int Height, int Percent)> sizes)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var result = new List<(byte[], int, int)>();
        var index = 0;
        foreach (var (width, height, _) in sizes)
        {
            index++;
            await page.SetViewportSizeAsync(width, height);
            var rows = string.Concat(Enumerable.Range(1, height / 26).Select(row =>
                $"<tr><td>{index:D2}-{row:D3}</td><td>Novák Jan</td><td>Příjem na oddělení {row % 7 + 1}</td><td>Čeká na potvrzení lékařem</td><td>12. 10. 2026 {row % 24:D2}:15</td></tr>"));
            await page.SetContentAsync($"<body style=\"margin:0;font:13px Segoe UI,Arial,sans-serif;color:#222\"><header style=\"background:#264b61;color:white;padding:8px 16px;font-size:15px\">Recepce · Obrazovka {index}</header><table style=\"width:100%;border-collapse:collapse\">{rows}</table><style>td{{border-bottom:1px solid #ddd;padding:4px 8px}}</style></body>");
            result.Add((await page.ScreenshotAsync(new() { Type = ScreenshotType.Png }), width, height));
        }
        return result;
    }

    private static List<(int Width, int Height)> EmbeddedImages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var images = new List<(int, int)>();
        foreach (Match dictionary in Regex.Matches(text, @"<<(?:(?!>>|<<).)*?/Subtype\s*/Image(?:(?!>>|<<).)*>>", RegexOptions.Singleline))
        {
            // A soft mask carries the alpha of an image already counted.
            if (Regex.IsMatch(dictionary.Value, @"/ColorSpace\s*/DeviceGray") && !Regex.IsMatch(dictionary.Value, @"/SMask")) continue;
            var width = Regex.Match(dictionary.Value, @"/Width\s+(\d+)");
            var height = Regex.Match(dictionary.Value, @"/Height\s+(\d+)");
            images.Add((int.Parse(width.Groups[1].Value), int.Parse(height.Groups[1].Value)));
        }
        return images;
    }

    private const string PrintScript = """
        window.atlasPrint = async snapshot => {
          const style = document.createElement('style');
          style.textContent = '@page{size:A4;margin:23mm 22mm}body{margin:0;width:166mm;font:10pt sans-serif}img{display:block;max-width:100%;max-height:220mm;object-fit:contain;margin:3mm auto}';
          document.head.append(style);
          const app = document.getElementById('app');
          for (const node of JSON.parse(snapshot.contentJson).document.content) {
            if (node.type === 'paragraph') app.append(Object.assign(document.createElement('p'), { textContent: node.content[0].text }));
            if (node.type === 'image') {
              const image = Object.assign(document.createElement('img'), { src: node.attrs.src, alt: node.attrs.alt });
              image.style.width = node.attrs.presentation.width + '%';
              app.append(image);
            }
          }
          await Promise.all(Array.from(app.querySelectorAll('img'), image => image.decode()));
        };
        """;

    /// <summary>Needs the Chromium headless shell of Microsoft.Playwright (docs/pdf-export.md, "Provoz a vývoj").</summary>
    public sealed class ChromiumFactAttribute : FactAttribute
    {
        public ChromiumFactAttribute()
        {
            // On CI a missing browser must fail the test, not hide it behind a skip.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            {
                try { ContentPdfRenderer.BrowserPath(); }
                catch (Atlas.Server.Platform.Validation.ApiException)
                {
                    // Review P2-10: the reason names what was looked for, so a skipped print test is never mistaken for a passed one.
                    Skip = "SKIPPED, no real PDF printed: no Chromium headless shell chromium_headless_shell-1255 in the Playwright browser cache (PLAYWRIGHT_BROWSERS_PATH) "
                        + "and no ATLAS_PDF_BROWSER. Install it with playwright.ps1 install --only-shell chromium; on CI (CI set) a missing browser fails instead.";
                }
            }
        }
    }
}
