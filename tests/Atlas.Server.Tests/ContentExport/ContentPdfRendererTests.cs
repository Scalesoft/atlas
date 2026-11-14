using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class ContentPdfRendererTests
{
    [Fact]
    public async Task One_pdf_at_a_time_another_is_refused_with_retry_after_and_the_slot_returns_after_cancellation_and_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-capacity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<html></html>");
        try
        {
            using var renderer = Renderer(root, NullLogger<ContentPdfRenderer>.Instance);
            using var cancelledStream = new BlockingManifestStream(ArchiveBytes());
            using var cancellation = new CancellationTokenSource();
            var cancelled = renderer.RenderAsync(Archive(cancelledStream), "https://wiki.invalid", PdfCoverDetails.All, cancellation.Token);
            await cancelledStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var rejected = new MemoryStream([1, 2, 3]);
            var busy = await Assert.ThrowsAsync<ApiException>(() => renderer.RenderAsync(Archive(rejected), "https://wiki.invalid", PdfCoverDetails.All, CancellationToken.None));
            Assert.Equal(503, busy.Status);
            Assert.Equal(ContentPdfRenderer.BusyCode, busy.Code);
            Assert.Equal("30", busy.Extensions!["retryAfter"]);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            using var failingStream = new BlockingManifestStream(ArchiveBytes());
            var failing = renderer.RenderAsync(Archive(failingStream), "https://wiki.invalid", PdfCoverDetails.All, CancellationToken.None);
            await failingStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            failingStream.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidDataException>(() => failing);
            using var available = new MemoryStream([1, 2, 3]);
            await Assert.ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(Archive(available), "https://wiki.invalid", PdfCoverDetails.All, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task An_interactive_export_waits_for_the_automatic_one_and_automatic_exports_yield_to_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-priority", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<html></html>");
        try
        {
            using var renderer = Renderer(root, NullLogger<ContentPdfRenderer>.Instance);
            var automatic = renderer.TryReserveAutomatic();
            Assert.NotNull(automatic);
            using var stream = new MemoryStream([1, 2, 3]);
            // Not refused: it waits for the automatic export's current item, and the runner takes no further one meanwhile.
            var interactive = renderer.RenderAsync(Archive(stream), "https://wiki.invalid", PdfCoverDetails.All, CancellationToken.None);
            Assert.False(interactive.IsCompleted);
            Assert.Null(renderer.TryReserveAutomatic());
            automatic.Dispose();
            // Served: it reached the render (the archive is no zip).
            await Assert.ThrowsAsync<InvalidDataException>(() => interactive.WaitAsync(TimeSpan.FromSeconds(30)));
            using var next = renderer.TryReserveAutomatic();
            Assert.NotNull(next);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_process_that_reused_the_browser_id_is_never_taken_for_the_browser()
    {
        // Started after the export launched its browser, but no headless browser: this test host.
        using var process = Process.GetCurrentProcess();
        Assert.False(ContentPdfRenderer.IsLaunchedBrowser(process, process.StartTime.ToUniversalTime()));
    }

    /// <summary>The image budget is decided from the headers: 80 full-width screenshots are refused without a browser.</summary>
    [Fact]
    public async Task Images_that_do_not_fit_even_at_150_dpi_are_refused_before_a_browser_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-refusal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A print page that never prints: reaching the browser would end in another error, never in this refusal.
        await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<html></html>");
        try
        {
            using var renderer = Renderer(root, NullLogger<ContentPdfRenderer>.Instance);
            var header = PngHeader(1920, 1080);
            var images = Enumerable.Repeat((header, 1920, 1080), 80).ToList();
            var refusal = await Assert.ThrowsAsync<ApiException>(() => renderer.RenderAsync(
                ContentPdfImageScalingTests.Archive(images, images.Select(_ => (1920, 1080, 100)).ToList()), "https://wiki.invalid/", PdfCoverDetails.All, CancellationToken.None));
            Assert.Equal(413, refusal.Status);
            Assert.Equal("pdf_images_too_large", refusal.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// The print page lays out (charts measure) under the print media with reduced motion, the light scheme and a hover pointer, as the
    /// browser's own print does; a failing check makes the print page throw, which the export reports as 422.
    /// </summary>
    [ContentPdfImageScalingTests.ChromiumFact]
    public async Task The_print_page_runs_under_print_media_reduced_motion_light_scheme_and_hover()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-media", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>Tisk</title></head><body><script src=\"/assets/print.js\"></script></body></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "print.js"), """
                window.atlasPrint = async () => {
                  const ok = ['print', '(prefers-reduced-motion: reduce)', '(prefers-color-scheme: light)', '(hover: hover)', '(pointer: fine)'].every(q => matchMedia(q).matches);
                  if (!ok || matchMedia('screen').matches || navigator.serviceWorker) throw new Error('media');
                };
                """);
            using var renderer = Renderer(root, NullLogger<ContentPdfRenderer>.Instance);
            var pdf = await renderer.RenderAsync(ContentPdfImageScalingTests.Archive([], []), "https://wiki.invalid/", PdfCoverDetails.All, CancellationToken.None);
            await using var stream = pdf.Stream;
            Assert.True(stream.Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [ContentPdfImageScalingTests.ChromiumFact]
    public async Task A_print_that_overruns_its_deadline_names_the_step_and_leaves_no_browser_running()
    {
        await WithStuckPrintAsync(async (renderer, archive, browser, log) =>
        {
            var timeout = await Assert.ThrowsAsync<ApiException>(() => renderer.RenderAsync(archive, "https://wiki.invalid/", PdfCoverDetails.All, CancellationToken.None));
            // Review P2-10: a failure under load says what the renderer logged (the browser's own exception is there).
            Assert.True((timeout.Status, timeout.Code) == (503, "pdf_timeout") && timeout.Message.Contains("(sestavení stránek)", StringComparison.Ordinal),
                $"{timeout.Status} {timeout.Code}: {timeout.Message}\n{Log(log)}");
            // The print left behind ended before the archive it reads was disposed.
            Assert.True(log.Any(x => x.Contains("PDF print ended with its browser", StringComparison.Ordinal)), Log(log));
            await AssertExitedAsync(await browser);
            // A deadline long enough that a loaded test host still reaches the stuck layout before it runs out (15 s ran out
            // during the browser start under the full parallel suite).
        }, TimeSpan.FromSeconds(45));
    }

    [ContentPdfImageScalingTests.ChromiumFact]
    public async Task A_cancelled_export_ends_its_browser()
    {
        await WithStuckPrintAsync(async (renderer, archive, browser, _) =>
        {
            using var cancellation = new CancellationTokenSource();
            var started = DateTime.UtcNow;
            var render = renderer.RenderAsync(archive, "https://wiki.invalid/", PdfCoverDetails.All, cancellation.Token);
            var id = await browser;
            // The process a kill would end is recognised as the export's browser.
            using (var process = Process.GetProcessById(id))
            {
                Assert.True(ContentPdfRenderer.IsLaunchedBrowser(process, started));
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
            await AssertExitedAsync(id);
        }, TimeSpan.FromMinutes(5));
    }

    /// <summary>A print page whose script never returns: a content module or a layout that does not finish.</summary>
    private static async Task WithStuckPrintAsync(Func<ContentPdfRenderer, ContentExportArchive, Task<int>, IEnumerable<string>, Task> act, TimeSpan deadline)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-stuck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "print.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>Tisk</title></head><body><script src=\"/assets/print.js\"></script></body></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "print.js"), "window.atlasPrint = async () => { for (;;) {} };");
            var capture = new CaptureLoggerProvider();
            using var logging = LoggerFactory.Create(x => x.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
            using var renderer = new ContentPdfRenderer(new AttachmentTests.EnvironmentStub(root) { WebRootPath = root }, Files(root), logging.CreateLogger<ContentPdfRenderer>()) { Deadline = deadline };
            await act(renderer, ContentPdfImageScalingTests.Archive([], []), BrowserAsync(capture), capture.Messages);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The browser process the renderer reports once it has started one.</summary>
    private static async Task<int> BrowserAsync(CaptureLoggerProvider capture)
    {
        for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromMinutes(2); await Task.Delay(50))
        {
            if (capture.Messages.Select(x => Regex.Match(x, @"PDF browser process (\d+)")).FirstOrDefault(x => x.Success) is { } found)
                return int.Parse(found.Groups[1].Value);
        }
        throw new TimeoutException("The renderer did not start a browser.\n" + Log(capture.Messages));
    }

    /// <summary>What the renderer logged, for the message of a failed assertion.</summary>
    private static string Log(IEnumerable<string> messages) => "Renderer log:\n" + string.Join("\n", messages);

    private static async Task AssertExitedAsync(int id)
    {
        for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(30); await Task.Delay(100))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited) return;
            }
            catch (ArgumentException) { return; }
        }
        Assert.Fail($"Browser process {id} still runs after the export ended.");
    }

    private static ContentPdfRenderer Renderer(string root, ILogger<ContentPdfRenderer> logger)
        => new(new AttachmentTests.EnvironmentStub(root) { WebRootPath = root }, Files(root), logger);

    private static ContentExportFiles Files(string root) => new(new ConfigurationBuilder().Build(), new AttachmentTests.EnvironmentStub(root));

    /// <summary>Signature and IHDR only: the budget reads nothing else.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    internal static ContentExportArchive Archive(Stream stream) => new(stream, "page.zip", "hash", Guid.NewGuid(), []);
    internal static byte[] ArchiveBytes()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open()))
            writer.Write("{}");
        return output.ToArray();
    }

    internal sealed class BlockingManifestStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new InvalidDataException("Controlled archive failure after entering the render.");
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
