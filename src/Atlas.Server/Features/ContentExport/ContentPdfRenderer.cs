using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Markdown.Sync;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// Prints only an already authorized immutable export, with no credentials or outbound HTTP. One export at a time, each in
/// its own Chromium: a long manual's print peaks at several hundred MiB across the browser's processes and takes every core
/// it is given, next to a server whose container has 1.5 GiB (docker-compose.yml). A further interactive export is refused at
/// once with Retry-After instead of waiting in a queue the client and the reverse proxy give up on; behind an automatic export
/// (<see cref="TryReserveAutomatic"/>) it waits for that one item, and automatic exports take no further one meanwhile. The whole
/// preparation, the wait included, has one deadline below the proxy's 150 s, and the browser is always gone when this returns:
/// closed, or killed when it does not close.
/// </summary>
public sealed class ContentPdfRenderer(IWebHostEnvironment environment, ContentExportFiles files, ILogger<ContentPdfRenderer> logger) : IContentPdfRenderer, IDisposable
{
    public const string BusyCode = "pdf_busy";
    public const int RetryAfterSeconds = 30;
    private const string Origin = "https://print.atlas.invalid";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim m_capacity = new(1, 1);
    private readonly PdfPrintImageCache m_printImages = new(Path.Combine(files.Root, "print-images"), environment);
    private readonly Lock m_gate = new();
    // Under m_gate: whether an automatic export holds the slot, and how many interactive exports wait for it.
    private bool m_automatic;
    private int m_waiting;

    /// <summary>
    /// The limit for everything from the browser's start to the finished PDF. With building the snapshot before and the
    /// download after, the answer stays under the reverse proxy's 150 s.
    /// </summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>How long an interactive export waits for the automatic export that holds the slot; it counts against <see cref="Deadline"/>.</summary>
    public TimeSpan AutomaticWait { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        bool wait;
        lock (m_gate)
        {
            wait = !m_capacity.Wait(0);
            if (wait && !m_automatic) throw Busy();
            if (wait) m_waiting++;
        }
        if (wait)
        {
            bool entered;
            try { entered = await m_capacity.WaitAsync(AutomaticWait, ct); }
            finally { lock (m_gate) m_waiting--; }
            if (!entered) throw Busy();
        }
        try { return await RenderCoreAsync(archive, sourceOrigin, coverDetails, Deadline - waited.Elapsed, ct); }
        finally { m_capacity.Release(); }
    }

    public IContentPdfSlot? TryReserveAutomatic()
    {
        lock (m_gate)
        {
            if (m_waiting > 0 || !m_capacity.Wait(0)) return null;
            m_automatic = true;
        }
        return new AutomaticSlot(this);
    }

    public void Dispose() => m_capacity.Dispose();

    private static ApiException Busy() => new(503, $"Server právě připravuje jiné PDF. Zkuste export znovu za {RetryAfterSeconds} sekund.", BusyCode,
        new Dictionary<string, object?> { ["retryAfter"] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture) });

    private sealed class AutomaticSlot(ContentPdfRenderer renderer) : IContentPdfSlot
    {
        private int m_released;

        public Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct)
            => renderer.RenderCoreAsync(archive, sourceOrigin, coverDetails, renderer.Deadline, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_released, 1) == 1) return;
            lock (renderer.m_gate)
            {
                renderer.m_automatic = false;
                renderer.m_capacity.Release();
            }
        }
    }

    private async Task<ContentExportArchive> RenderCoreAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, TimeSpan limit, CancellationToken ct)
    {
        var root = environment.WebRootPath;
        if (environment.IsDevelopment() && (root is null || !File.Exists(Path.Combine(root, "print.html"))))
        {
            root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../Atlas.Web/dist"));
        }
        if (root is null || !File.Exists(Path.Combine(root, "print.html")))
        {
            throw new ApiException(503, "Tisková sestava není dostupná. Správce musí sestavit webovou aplikaci.");
        }
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
        await using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var data = manifest.RootElement;
        var page = data.GetProperty("pages").Deserialize<ExportPage[]>(Json)!.Single();
        using var reader = new StreamReader(zip.GetEntry(page.ContentJsonPath)!.Open());
        var snapshot = new
        {
            page.Title, Space = data.GetProperty("space").GetProperty("name").GetString(), page.Version,
            page.IsPublished, page.ActiveFrom, ExportedAt = data.GetProperty("exportedAt").GetDateTime(),
            SourceOrigin = sourceOrigin, PageId = page.Id, Details = PdfCoverDetails.Normalize(coverDetails), ContentJson = await reader.ReadToEndAsync(ct)
        };
        var attachments = data.GetProperty("attachments").Deserialize<ExportAttachment[]>(Json)!
            .ToDictionary(x => new Uri(new Uri(Origin), x.CanonicalUrl).AbsolutePath, StringComparer.Ordinal);
        if (attachments.Count > 100 || attachments.Values.Sum(x => x.Size) > 64L * 1024 * 1024)
        {
            throw new ApiException(413, "PDF podporuje nejvýše 100 příloh o celkové velikosti 64 MiB. Rozdělte dokument na menší stránky.");
        }
        var imageBudget = new PdfImageBudget();
        PdfImagePlan imagePlan;
        using (var content = JsonDocument.Parse(snapshot.ContentJson, MarkdownDocumentValidator.JsonOptions))
        {
            imageBudget.CheckEmbedded(content.RootElement);
            imagePlan = PdfImagePlan.Read(content.RootElement, source => ImagePath(attachments, source));
        }
        // Decided from the headers before any browser starts: a document that cannot fit is refused here, in milliseconds.
        await FitImagesAsync(imagePlan, zip, attachments, imageBudget.Pixels, ct);
        var trace = new RenderTrace(imagePlan.Paths.Count());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit > TimeSpan.Zero ? limit : TimeSpan.Zero);
        try
        {
            var bytes = await BrowseAsync(root, zip, attachments, snapshot, page, snapshot.Details.Contains(PdfCoverDetails.Revision), imageBudget, imagePlan, trace, deadline.Token);
            logger.LogInformation("PDF of page {PageId}: {Scaled} of {Images} images scaled at {Dpi} DPI ({Cached} from the cache) in {ScaleMs} ms; browser {LaunchMs} ms, layout {LayoutMs} ms, print {PrintMs} ms, total {TotalMs} ms",
                page.Id, trace.Scaled, trace.Images, imagePlan.Dpi, trace.Cached, trace.ScaleMs, trace.LaunchMs, trace.LayoutMs, trace.PrintMs, trace.TotalMs);
            var output = files.Create();
            try
            {
                await output.WriteAsync(bytes, ct);
                output.Position = 0;
                return archive with { Stream = output, Name = Path.ChangeExtension(archive.Name, ".pdf"), Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            }
            catch { await output.DisposeAsync(); throw; }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PDF of page {PageId} exceeded {Deadline} s during {Phase}: {Scaled} of {Images} images scaled at {Dpi} DPI ({Cached} from the cache) in {ScaleMs} ms, browser {LaunchMs} ms, layout {LayoutMs} ms",
                page.Id, Deadline.TotalSeconds, trace.Phase, trace.Scaled, trace.Images, imagePlan.Dpi, trace.Cached, trace.ScaleMs, trace.LaunchMs, trace.LayoutMs);
            throw new ApiException(503, $"Příprava PDF překročila časový limit {Deadline.TotalSeconds:0} s ({trace.Phase}). Dokument je na tisk příliš náročný "
                + "nebo je server vytížený. Obrázky zmenšené pro tisk zůstávají uložené, takže další pokus naváže tam, kde tento skončil: "
                + "zkuste export znovu, případně stránku rozdělte na kratší části.", "pdf_timeout");
        }
        catch (PuppeteerException error)
        {
            logger.LogWarning(error, "PDF export failed for page {PageId} during {Phase}", page.Id, trace.Phase);
            throw new ApiException(503, "PDF se nepodařilo připravit. Ověřte dostupnost obrázků a obsahových modulů; pokud problém trvá, kontaktujte správce.");
        }
    }

    /// <summary>Runs the export's own browser and makes sure it is gone afterwards, whatever ends the work.</summary>
    private async Task<byte[]> BrowseAsync(string root, ZipArchive zip, Dictionary<string, ExportAttachment> attachments, object snapshot,
        ExportPage document, bool revisionInHeader, PdfImageBudget imageBudget, PdfImagePlan imagePlan, RenderTrace trace, CancellationToken ct)
    {
        IBrowser? browser = null;
        int? browserProcess = null;
        Task<byte[]>? print = null;
        var launched = DateTime.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var executable = BrowserPath();
            // Over CDP from .NET, no Node.js driver. On Linux through a pipe (no port), as Playwright did.
            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true, ExecutablePath = executable, Timeout = 15000, Pipe = OperatingSystem.IsLinux(),
                // The flags Playwright adds that matter to a print: no sandbox, the small /dev/shm of a container, sRGB colours and no
                // scroll bars; the print's own device scale is 1 as before. Chromium's sandbox needs user namespaces (or a setuid helper the
                // headless shell does not ship), which Docker's default seccomp profile and the nested LXC of production refuse; the
                // browser runs as the image's non-root user app instead (Dockerfile), so a compromised renderer holds only its rights.
                Args = ["--no-sandbox", "--disable-dev-shm-usage", "--hide-scrollbars", "--export-tagged-pdf", "--disable-back-forward-cache", "--disable-field-trial-config",
                    "--blink-settings=primaryHoverType=2,availableHoverTypes=2,primaryPointerType=4,availablePointerTypes=4"]
            });
            browserProcess = browser.Process?.Id;
            logger.LogDebug("PDF browser process {ProcessId}", browserProcess);
            trace.LaunchMs = trace.Lap();
            trace.Phase = "zmenšení obrázků";
            // The browser does not observe the token: leaving here closes it, which ends the work inside it.
            print = PrintAsync(browser, root, zip, attachments, snapshot, document, revisionInHeader, imageBudget, imagePlan, trace, ct);
            return await print.WaitAsync(ct);
        }
        finally
        {
            await ShutDownAsync(browser, browserProcess, launched);
            // Left by the deadline or a cancellation: the print is waited for and observed before the archive it reads is disposed.
            if (print is not null && ct.IsCancellationRequested) await AbandonAsync(print);
        }
    }

    /// <summary>
    /// A print left behind by the deadline or a cancellation ends with its browser; it must be over before the caller disposes the
    /// archive it reads. Its failure is the closed browser's and the export has its answer already.
    /// </summary>
    private async Task AbandonAsync(Task print)
    {
        try
        {
            await print.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) when (!print.IsCompleted)
        {
            // Whatever it touches next is disposed and throws: observed here, never an unobserved task exception.
            _ = print.ContinueWith(x => x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            logger.LogWarning("PDF print did not end within 5 s of its browser");
            return;
        }
        catch (Exception) when (print.IsCompleted)
        {
            // The closed browser's failure: the export has its answer already.
        }
        logger.LogDebug("PDF print ended with its browser");
    }

    /// <summary>
    /// The Chromium headless shell the export prints with: <c>ATLAS_PDF_BROWSER</c> (the image's niced wrapper, Dockerfile), else the
    /// <c>chromium_headless_shell-1248</c> of Playwright's browser cache (<c>playwright.ps1 install --only-shell chromium</c>) for
    /// development and tests.
    /// </summary>
    public static string BrowserPath()
    {
        // The shell revision the Dockerfile ships (CHROMIUM_REVISION), so development and tests print with the same Chromium.
        const string ChromiumRevision = "1248";
        if (Environment.GetEnvironmentVariable("ATLAS_PDF_BROWSER") is { Length: > 0 } configured) return configured;
        var cache = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom ? custom
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
            : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "ms-playwright")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        var shell = Directory.Exists(cache)
            ? Directory.GetDirectories(cache, "chromium_headless_shell-" + ChromiumRevision)
                .SelectMany(x => Directory.EnumerateFiles(x, "*", SearchOption.AllDirectories))
                .FirstOrDefault(x => Path.GetFileName(x) is "chrome-headless-shell" or "chrome-headless-shell.exe" or "headless_shell")
            : null;
        return shell ?? throw new ApiException(503, "Prohlížeč pro tisk PDF není nainstalován. Správce musí nastavit ATLAS_PDF_BROWSER.");
    }

    private async Task ShutDownAsync(IBrowser? browser, int? browserProcess, DateTime launched)
    {
        var closed = browser is null;
        if (browser is not null)
        {
            try
            {
                await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
                closed = true;
            }
            catch (Exception error)
            {
                // Whatever the close throws, the kill and the wait for the print below still run.
                logger.LogWarning("PDF browser did not close ({Error})", error.GetType().Name);
            }
        }
        if (!closed && browserProcess is { } id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (IsLaunchedBrowser(process, launched))
                {
                    process.Kill(entireProcessTree: true);
                    logger.LogWarning("PDF browser process {ProcessId} was killed", id);
                }
                else
                {
                    logger.LogWarning("PDF browser process {ProcessId} is gone; the process now holding its id was left running", id);
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException
                or IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Whether <paramref name="process"/> is the headless browser an export launched at <paramref name="launched"/>, so a kill never
    /// hits a process that has since reused its id: started with the export, a Chromium executable and, on Linux, driven over
    /// the DevTools pipe (its command line).
    /// </summary>
    public static bool IsLaunchedBrowser(Process process, DateTime launched)
    {
        if (process.StartTime.ToUniversalTime() < launched.AddSeconds(-1)) return false;
        if (OperatingSystem.IsLinux())
        {
            var arguments = File.ReadAllText($"/proc/{process.Id.ToString(CultureInfo.InvariantCulture)}/cmdline").Split('\0');
            return IsBrowserName(Path.GetFileName(arguments[0])) && arguments.Contains("--remote-debugging-pipe");
        }
        return IsBrowserName(process.ProcessName);
    }

    // Chromium, its headless shell (chrome-headless-shell, headless_shell) or Chrome.
    private static bool IsBrowserName(string name)
        => name.Contains("chrom", StringComparison.OrdinalIgnoreCase) || name.Contains("headless", StringComparison.OrdinalIgnoreCase);

    /// <summary>The attachment key of an image source the print page would request from this export, if it is one.</summary>
    private static string? ImagePath(Dictionary<string, ExportAttachment> attachments, string source)
    {
        if (source.Length == 0 || source.Length > 2048 || !Uri.TryCreate(new Uri(Origin), source.Trim(), out var uri)
            || uri.GetLeftPart(UriPartial.Authority) != Origin || uri.Query.Length != 0) return null;
        return attachments.TryGetValue(uri.AbsolutePath, out var attachment) && attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath : null;
    }

    /// <summary>
    /// Reads only the headers of the images the document uses and lets the plan choose the print resolution at which they
    /// fit together. An image that cannot be measured is left to the check at the request, which refuses it if it prints.
    /// </summary>
    private static async Task FitImagesAsync(PdfImagePlan plan, ZipArchive zip, Dictionary<string, ExportAttachment> attachments, long admittedPixels, CancellationToken ct)
    {
        var originals = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        foreach (var path in plan.Paths)
        {
            var bytes = await ReadAsync(zip, attachments[path], ct);
            try { originals[path] = PdfImageBudget.Measure(bytes); }
            catch (ApiException) { }
        }
        plan.Fit(originals, admittedPixels);
    }

    private static async Task<byte[]> ReadAsync(ZipArchive zip, ExportAttachment attachment, CancellationToken ct)
    {
        await using var input = zip.GetEntry(attachment.ArchivePath)!.Open();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task<byte[]> PrintAsync(IBrowser browser, string root, ZipArchive zip, Dictionary<string, ExportAttachment> attachments,
        object snapshot, ExportPage document, bool revisionInHeader, PdfImageBudget imageBudget, PdfImagePlan imagePlan, RenderTrace trace, CancellationToken ct)
    {
        // What the print page receives for each image: its copy at the printed size (kept in the cache), or the original.
        var printedImages = new Dictionary<string, (byte[] Bytes, string MimeType)>(StringComparer.Ordinal);
        // Every image once, before the print page exists, one original decoded at a time; the scaler's context and its
        // renderer process are gone before the layout and the print, the export's memory peak. A copy an earlier export
        // made is taken from the cache, and the scaler only starts for the first image that has none.
        m_printImages.Prune();
        PdfImageScaler? scaler = null;
        try
        {
            var index = 0;
            foreach (var path in imagePlan.Paths)
            {
                trace.Phase = $"zmenšení obrázků, {++index}. z {trace.Images}";
                var attachment = attachments[path];
                var original = await ReadAsync(zip, attachment, ct);
                int width, height;
                // An original that cannot be measured is refused when the print page asks for it; only a link may name it.
                try { (width, height) = PdfImageBudget.Measure(original, attachment.Name); }
                catch (ApiException) { continue; }
                // JPEG orientation may turn the decoded image; the scaler picks the factor that matches what it decoded.
                var (scale, turnedScale) = (imagePlan.Scale(path, width, height), imagePlan.Scale(path, height, width));
                var key = scale < 1 || turnedScale < 1 ? PdfPrintImageCache.Key(original, width, height, scale, turnedScale) : null;
                var scaled = key is null ? null : m_printImages.Get(key);
                if (scaled is not null) trace.Cached++;
                else if (key is not null)
                {
                    scaler ??= await PdfImageScaler.CreateAsync(browser, Origin);
                    scaled = await scaler.ScaleAsync(original, attachment.MimeType, width, scale, turnedScale, attachment.Name);
                    if (scaled is { } copy) m_printImages.Put(key, copy.Bytes, copy.MimeType);
                }
                var image = scaled ?? (original, attachment.MimeType);
                // The renderer's budget counts what it receives: the scaled copy, not the original.
                imageBudget.Add(image.Bytes, attachment.Name);
                printedImages[path] = image;
                if (scaled is not null) trace.Scaled++;
            }
        }
        finally
        {
            if (scaler is not null) await scaler.DisposeAsync();
        }
        trace.ScaleMs = trace.Lap();
        trace.Phase = "sestavení stránek";
        // A context of its own (no shared cache or cookies), closed with the browser.
        var context = await browser.CreateBrowserContextAsync();
        var failures = new ConcurrentQueue<string>();
        var resourceErrors = new ConcurrentQueue<ApiException>();
        // Serialize ZipArchive access: concurrent image requests must not seek the same backing stream.
        using var zipGate = new SemaphoreSlim(1);
        var page = await context.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 794, Height = 1123 });
        await page.EmulateTimezoneAsync("Europe/Prague");
        await page.Client.SendAsync("Emulation.setLocaleOverride", new { locale = "cs-CZ" });
        // No service worker can register (the page never sees the API), and no other page of the context lives: a popup would not be intercepted.
        await page.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = "delete Navigator.prototype.serviceWorker;" });
        page.Popup += (_, e) => _ = e.PopupPage.CloseAsync().ContinueWith(x => x.Exception, TaskScheduler.Default);
        await page.SetRequestInterceptionAsync(true);
        page.Request += (_, e) => _ = RouteAsync(e.Request);
        async Task RouteAsync(IRequest request)
        {
            try { await RouteCoreAsync(request); }
            catch (Exception)
            {
                // A handler that fails must not leave the request hanging until the deadline: record it and abort what is left.
                failures.Enqueue("Resource handler failed");
                try { await request.AbortAsync(); } catch (Exception) { /* answered already, or the browser is closing */ }
            }
        }
        async Task RouteCoreAsync(IRequest route)
        {
            var uri = new Uri(route.Url);
            // Inline data and blob URLs (a module's worker) never leave the page.
            if (uri.Scheme is "data" or "blob") { await route.ContinueAsync(); return; }
            if (!resourceErrors.IsEmpty || uri.GetLeftPart(UriPartial.Authority) != Origin || route.Method != HttpMethod.Get)
            {
                failures.Enqueue("External resource");
                await route.AbortAsync();
                return;
            }
            var path = uri.AbsolutePath;
            if (uri.Query.Length == 0 && attachments.TryGetValue(path, out var attachment) && route.ResourceType == ResourceType.Image)
            {
                await zipGate.WaitAsync();
                try
                {
                    if (!printedImages.TryGetValue(path, out var image))
                    {
                        // shortcut: an image the document's JSON never names (a module building its URL) prints as stored and counts
                        // whole against the budget; scale it as well if a module ever does that.
                        var original = await ReadAsync(zip, attachment, CancellationToken.None);
                        PdfImageBudget.Measure(original, attachment.Name);
                        imageBudget.Add(original, attachment.Name);
                        printedImages[path] = image = (original, attachment.MimeType);
                    }
                    await route.RespondAsync(new ResponseData { BodyData = image.Bytes, ContentType = image.MimeType });
                }
                catch (ApiException error)
                {
                    resourceErrors.Enqueue(error);
                    await route.AbortAsync();
                }
                finally { zipGate.Release(); }
                return;
            }
            // Only build output names; never allow path traversal, document-supplied script or filesystem URLs.
            if (path == "/print.html" || path.StartsWith("/assets/", StringComparison.Ordinal)
                && path[8..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !path.Contains("..", StringComparison.Ordinal))
            {
                var file = Path.Combine(root, path.TrimStart('/'));
                var mime = Path.GetExtension(file) switch
                {
                    ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css",
                    ".woff2" => "font/woff2", ".woff" => "font/woff", ".wasm" => "application/wasm", _ => null
                };
                if (mime is not null && File.Exists(file))
                {
                    await route.RespondAsync(new ResponseData { BodyData = await File.ReadAllBytesAsync(file, CancellationToken.None), ContentType = mime, Headers = new Dictionary<string, object>
                    {
                        ["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; worker-src blob:; base-uri 'none'; form-action 'none'",
                        ["X-Content-Type-Options"] = "nosniff"
                    } });
                    return;
                }
            }
            failures.Enqueue("Unavailable resource");
            await route.AbortAsync();
        }
        page.PageError += (_, _) => failures.Enqueue("Rendering error");
        page.RequestFailed += (_, _) => failures.Enqueue("Resource failed");
        // One call: PuppeteerSharp's own media-feature call would reset the print media type set just before it.
        await page.Client.SendAsync("Emulation.setEmulatedMedia", new
        {
            media = "print",
            features = new[] { new { name = "prefers-reduced-motion", value = "reduce" }, new { name = "prefers-color-scheme", value = "light" } }
        });
        await page.GoToAsync(Origin + "/print.html", new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = 15000 });
        await page.WaitForFunctionAsync("() => typeof window.atlasPrint === 'function'", new WaitForFunctionOptions { Timeout = 15000 });
        var renderError = await page.EvaluateFunctionAsync<bool>("async json => { try { await window.atlasPrint(JSON.parse(json)); return false; } catch { return true; } }", JsonSerializer.Serialize(snapshot, Json));
        if (resourceErrors.TryDequeue(out var resourceError)) throw resourceError;
        if (renderError) throw new ApiException(422, "Některý obrázek nebo obsahový modul nelze připravit pro tisk. Zkontrolujte obsah stránky.");
        if (!failures.IsEmpty) throw new ApiException(422, "PDF obsahuje nedostupné nebo externí zdroje. Obrázky nejdříve nahrajte jako přílohy stránky.");
        trace.LayoutMs = trace.Lap();
        trace.Phase = "tisk do PDF";
        var title = WebUtility.HtmlEncode(document.Title);
        var header = $"<div style='font-family:sans-serif;font-size:8px;color:#526b7b;width:100%;margin:0 22mm;display:flex;justify-content:space-between;border-bottom:1px solid #d7e0e5;padding-bottom:5px'><span style='max-width:80%;overflow:hidden;white-space:nowrap'>{title}</span><span>{(revisionInHeader ? $"Revize {document.Version}" : "")}</span></div>";
        var footer = "<div style='font-family:sans-serif;font-size:8px;color:#526b7b;width:100%;margin:0 22mm;text-align:right'><span class='pageNumber'></span> / <span class='totalPages'></span></div>";
        var pdf = await page.PdfDataAsync(new PdfOptions { Format = PaperFormat.A4, PreferCSSPageSize = true, PrintBackground = true,
            DisplayHeaderFooter = true, HeaderTemplate = header, FooterTemplate = footer, Tagged = true, Outline = true });
        if (!failures.IsEmpty) throw new ApiException(422, "Některou část dokumentu nelze vytisknout. Zkontrolujte obsahové moduly stránky.");
        trace.PrintMs = trace.Lap();
        return pdf;
    }

    /// <summary>Where an export spends its time: for the log, and for a timeout message that names the step it stopped in.</summary>
    private sealed class RenderTrace(int images)
    {
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private long m_lap;

        public int Images { get; } = images;
        public int Scaled { get; set; }
        public int Cached { get; set; }
        public string Phase { get; set; } = "spuštění prohlížeče";
        public long LaunchMs { get; set; }
        public long ScaleMs { get; set; }
        public long LayoutMs { get; set; }
        public long PrintMs { get; set; }
        public long TotalMs => m_clock.ElapsedMilliseconds;

        /// <summary>Milliseconds since the previous lap.</summary>
        public long Lap()
        {
            var now = m_clock.ElapsedMilliseconds;
            var lap = now - m_lap;
            m_lap = now;
            return lap;
        }
    }
}
