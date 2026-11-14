using PuppeteerSharp;

namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// Scales an attachment image to its printed size in the export's own Chromium, before the print page receives it. The
/// decoder that already prints the document does the work, sandboxed and without another image library in the server:
/// a separate context with no network, whose only resource is the one original being scaled. It decodes one original
/// at a time (the caller serializes and has refused anything over <see cref="PdfImageBudget.MaximumImagePixels"/> from its
/// header), draws it with high-quality smoothing and encodes PNG, or JPEG for a JPEG original. The copy lives only in
/// the print-image cache (<see cref="PdfPrintImageCache"/>); the stored attachment and its download are untouched.
/// </summary>
internal sealed class PdfImageScaler : IAsyncDisposable
{
    private const string Script = """
        async ({ width, scale, turnedScale, type }) => {
          const blob = await (await fetch('/source')).blob();
          const image = await createImageBitmap(blob);
          try {
            const factor = image.width === width ? scale : turnedScale;
            if (factor >= 1) return null;
            const canvas = new OffscreenCanvas(Math.max(1, Math.round(image.width * factor)), Math.max(1, Math.round(image.height * factor)));
            const context = canvas.getContext('2d');
            context.imageSmoothingEnabled = true;
            context.imageSmoothingQuality = 'high';
            context.drawImage(image, 0, 0, canvas.width, canvas.height);
            const output = await canvas.convertToBlob(type === 'image/jpeg' ? { type, quality: 0.92 } : { type });
            const url = await new Promise((resolve, reject) => {
              const reader = new FileReader();
              reader.onload = () => resolve(reader.result);
              reader.onerror = () => reject(reader.error);
              reader.readAsDataURL(output);
            });
            return url.slice(url.indexOf(',') + 1);
          } finally { image.close(); }
        }
        """;

    private readonly IBrowserContext m_context;
    private readonly string m_origin;
    private IPage m_page = null!;
    private byte[]? m_source;
    private string m_sourceType = "";

    private PdfImageScaler(IBrowserContext context, string origin)
    {
        m_context = context;
        m_origin = origin;
    }

    public static async Task<PdfImageScaler> CreateAsync(IBrowser browser, string origin)
    {
        var context = await browser.CreateBrowserContextAsync();
        try
        {
            var scaler = new PdfImageScaler(context, origin);
            scaler.m_page = await context.NewPageAsync();
            await scaler.m_page.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = "delete Navigator.prototype.serviceWorker;" });
            await scaler.m_page.SetRequestInterceptionAsync(true);
            scaler.m_page.Request += (_, e) => _ = scaler.RouteAsync(e.Request);
            await scaler.m_page.GoToAsync(origin + "/scale", new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = 15000 });
            return scaler;
        }
        catch
        {
            await context.CloseAsync();
            throw;
        }
    }

    /// <summary>
    /// The copy at the printed size, or null when the factor for the decoded orientation leaves the image as it is.
    /// <paramref name="turnedScale"/> applies when the decoded image is turned against its header (JPEG orientation).
    /// </summary>
    public async Task<(byte[] Bytes, string MimeType)?> ScaleAsync(byte[] original, string mimeType, int width, double scale, double turnedScale, string name)
    {
        if (scale >= 1 && turnedScale >= 1) return null;
        var type = mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        m_source = original;
        m_sourceType = mimeType;
        try
        {
            var encoded = await m_page.EvaluateFunctionAsync<string?>(Script, new { width, scale, turnedScale, type });
            return encoded is null ? null : (Convert.FromBase64String(encoded), type);
        }
        catch (PuppeteerException)
        {
            throw new ApiException(422, "Obrázek „{name}“ nelze připravit pro tisk. Uložte ho znovu jako PNG nebo JPEG.", args: new Dictionary<string, object?> { ["name"] = name });
        }
        finally { m_source = null; }
    }

    public async ValueTask DisposeAsync() => await m_context.CloseAsync();

    private async Task RouteAsync(IRequest route)
    {
        try
        {
            var uri = new Uri(route.Url);
            if (uri.Scheme is "data" or "blob") { await route.ContinueAsync(); return; }
            if (uri.GetLeftPart(UriPartial.Authority) == m_origin && route.Method == HttpMethod.Get && uri.Query.Length == 0)
            {
                if (uri.AbsolutePath == "/scale")
                {
                    await route.RespondAsync(new ResponseData
                    {
                        Body = "<!doctype html><html lang=\"cs\"><head><meta charset=\"utf-8\"><title>Atlas</title></head><body></body></html>", ContentType = "text/html",
                        Headers = new Dictionary<string, object> { ["Content-Security-Policy"] = "default-src 'none'; connect-src 'self'", ["X-Content-Type-Options"] = "nosniff" }
                    });
                    return;
                }
                if (uri.AbsolutePath == "/source" && m_source is { } source)
                {
                    await route.RespondAsync(new ResponseData { BodyData = source, ContentType = m_sourceType });
                    return;
                }
            }
            await route.AbortAsync();
        }
        catch (Exception)
        {
            // The scaling then fails at its fetch at once instead of waiting for the deadline.
            try { await route.AbortAsync(); } catch (Exception) { /* answered already, or the browser is closing */ }
        }
    }
}
