using System.Text.Json;

namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// How many pixels each attachment image needs on paper, so the PDF gets them at their printed size instead of at the
/// resolution of the original. Screenshots are routinely full HD or larger and print 166 mm wide, so a long manual's
/// originals exceed what the renderer may decode at once (<see cref="PdfImageBudget"/>) although the same images at
/// the size they occupy on the page fit easily. The plan reads the printed width from the document itself: the image
/// node's <c>attrs.presentation</c> (width in percent of the text column, crop and rotation once the editor recorded the
/// natural size; @atlas/markdown features/images/presentation.ts) and the text column and maximum image height of
/// the print template (src/Atlas.Web/src/print/print.css). It picks the highest resolution between 300 and 150 DPI at
/// which all images fit the renderer's budget; 200 DPI keeps 12–13 px screenshot text sharp, 150 DPI is the floor a printed
/// page still reads at. An image never grows. It decides from the image headers before any browser starts, so a document
/// that cannot fit is refused at once with the numbers, never after a timeout.
/// </summary>
public sealed class PdfImagePlan
{
    public const int MaximumDpi = 300;
    public const int MinimumDpi = 150;
    private const double ColumnInches = 166 / 25.4;
    private const double MaximumHeightInches = 220 / 25.4;
    private readonly Dictionary<string, List<Usage>> m_usages;

    private PdfImagePlan(Dictionary<string, List<Usage>> usages) => m_usages = usages;

    /// <summary>The attachment images the document uses.</summary>
    public IEnumerable<string> Paths => m_usages.Keys;

    /// <summary>The DPI the images are scaled to; <see cref="MaximumDpi"/> until <see cref="Fit"/> lowers it.</summary>
    public int Dpi { get; private set; } = MaximumDpi;

    /// <summary>
    /// Collects every use of the given attachment paths: an image node with its presentation, and any other string that
    /// names the attachment (another module showing it) as the full text column, so nothing is ever printed below its plan.
    /// </summary>
    public static PdfImagePlan Read(JsonElement content, Func<string, string?> attachmentPath)
    {
        var usages = new Dictionary<string, List<Usage>>(StringComparer.Ordinal);
        Collect(content, attachmentPath, usages);
        return new PdfImagePlan(usages);
    }

    /// <summary>
    /// Lowers <see cref="Dpi"/> in steps until the planned images, with <paramref name="admittedPixels"/> already on the
    /// page, fit the renderer's total. Refuses the export (413) when they do not fit even at <see cref="MinimumDpi"/>,
    /// with what the images would need, the limit and what to change.
    /// </summary>
    public void Fit(IReadOnlyDictionary<string, (int Width, int Height)> originals, long admittedPixels)
    {
        var total = 0L;
        for (Dpi = MaximumDpi; Dpi >= MinimumDpi; Dpi -= 25)
        {
            total = admittedPixels;
            foreach (var (path, (width, height)) in originals)
            {
                var (scaledWidth, scaledHeight) = Size(width, height, Scale(path, width, height));
                total += (long)scaledWidth * scaledHeight;
            }
            if (total <= PdfImageBudget.MaximumTotalPixels) return;
        }
        Dpi = MinimumDpi;
        var original = originals.Values.Sum(x => (long)x.Width * x.Height);
        throw new ApiException(413, $"Obrázky se do PDF nevejdou ani při nejnižším čitelném rozlišení {MinimumDpi} DPI: po zmenšení na tiskovou velikost "
            + $"by měly {Millions(total)} mil. pixelů, limit PDF je {Millions(PdfImageBudget.MaximumTotalPixels)} mil. ({originals.Count} obrázků, originály "
            + $"{Millions(original)} mil. pixelů). Rozdělte stránku na kratší stránky, nebo ve stránce zmenšete šířku či ořízněte velké obrázky.", "pdf_images_too_large");
    }

    private static string Millions(long pixels) => (pixels / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("cs-CZ"));

    /// <summary>The factor (at most 1) by which an image of these decoded dimensions is scaled for print.</summary>
    public double Scale(string path, int width, int height)
    {
        // An image the document names nowhere the plan can see is planned as the full text column.
        var usages = m_usages.TryGetValue(path, out var found) ? found : [Usage.Column];
        var inchesPerPixel = usages.Max(x => x.InchesPerPixel(width, height));
        return Math.Min(1, Dpi * inchesPerPixel);
    }

    public static (int Width, int Height) Size(int width, int height, double scale)
        => scale >= 1 ? (width, height) : (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));

    private static void Collect(JsonElement node, Func<string, string?> attachmentPath, Dictionary<string, List<Usage>> usages)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("image")
                    && node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String
                    && attachmentPath(src.GetString()!) is { } imagePath)
                {
                    Add(usages, imagePath, Usage.Of(attrs.TryGetProperty("presentation", out var presentation) ? presentation : default));
                    foreach (var property in node.EnumerateObject().Where(x => x.Name != "attrs")) Collect(property.Value, attachmentPath, usages);
                    break;
                }
                foreach (var property in node.EnumerateObject()) Collect(property.Value, attachmentPath, usages);
                break;
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray()) Collect(child, attachmentPath, usages);
                break;
            case JsonValueKind.String:
                if (attachmentPath(node.GetString()!) is { } path) Add(usages, path, Usage.Column);
                break;
        }
    }

    private static void Add(Dictionary<string, List<Usage>> usages, string path, Usage usage)
    {
        if (!usages.TryGetValue(path, out var list)) usages[path] = list = [];
        list.Add(usage);
    }

    /// <summary>
    /// One printed use. Without the recorded natural size the renderer ignores crop and rotation and the template caps the
    /// height; with it, the crop viewport is the printed width and shows only the uncropped part of the source.
    /// </summary>
    private sealed record Usage(double Inches, double VisibleX, double VisibleY, bool Turned, bool HeightCapped)
    {
        public static readonly Usage Column = new(ColumnInches, 1, 1, false, false);

        public static Usage Of(JsonElement presentation)
        {
            if (presentation.ValueKind != JsonValueKind.Object) return Column with { HeightCapped = true };
            var width = Math.Clamp(Number(presentation, "width", 100), 10, 100);
            var inches = ColumnInches * width / 100;
            if (Number(presentation, "naturalWidth", 0) <= 0) return new Usage(inches, 1, 1, false, true);
            var visibleX = 1 - Math.Clamp(Number(presentation, "cropLeft", 0) + Number(presentation, "cropRight", 0), 0, 95) / 100;
            var visibleY = 1 - Math.Clamp(Number(presentation, "cropTop", 0) + Number(presentation, "cropBottom", 0), 0, 95) / 100;
            return new Usage(inches, visibleX, visibleY, Number(presentation, "rotate", 0) % 180 != 0, false);
        }

        public double InchesPerPixel(int width, int height)
        {
            var across = Turned ? Inches / (height * VisibleY) : Inches / (width * VisibleX);
            return HeightCapped ? Math.Min(across, MaximumHeightInches / height) : across;
        }

        private static double Number(JsonElement presentation, string name, double fallback)
            => presentation.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
    }
}
