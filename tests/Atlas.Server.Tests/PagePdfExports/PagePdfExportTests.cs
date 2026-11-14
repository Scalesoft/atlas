using System.Text;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.PagePdfExports;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Tests.ContentExport;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.PagePdfExports;

public sealed class PagePdfExportTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Saved_revisions_debounce_coalesce_and_replace_unless_history_is_requested()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var first = await f.SaveRule();
        Assert.False(first.Settings.KeepVersions);
        await f.Run(DateTime.UtcNow);
        Assert.Equal(0, f.Pdf.Calls);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(1, f.Pdf.Calls);
        Assert.Single(Directory.GetFiles(f.Target));
        await f.Save("second");
        await f.Save("third");
        await f.Run(DateTime.UtcNow);
        Assert.Equal(1, f.Pdf.Calls);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(2, f.Pdf.Calls);
        Assert.Single(Directory.GetFiles(f.Target));
        var state = await f.Host.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(f.Page.Id));
        Assert.Equal("current", state.Status);
        Assert.Equal(state.LastRevisionId!.Value.ToString(), await File.ReadAllTextAsync(f.FileOf("document.pdf")));
        await f.SaveRule(true, first.Settings.Version);
        await f.Run(DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(2, Directory.GetFiles(f.Target).Length);
    }

    /// <summary>The job's data schedule (PagePdfExportJob): a saved rule is due when its 30 s pass, "Exportovat teď" at once, and
    /// nothing once the export is current.</summary>
    [PostgreSqlFact]
    public async Task The_next_export_is_due_when_its_revision_settles_and_never_once_it_is_current()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var runner = f.Host.Services.GetRequiredService<PagePdfExportRunner>();
        Assert.Null(await runner.NextDueAsync(CancellationToken.None));
        var saved = DateTime.UtcNow;
        await f.SaveRule();
        Assert.InRange((await runner.NextDueAsync(CancellationToken.None))!.Value, saved.AddSeconds(30).AddMilliseconds(-1), DateTime.UtcNow.AddSeconds(30));
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(1, f.Pdf.Calls);
        Assert.Null(await runner.NextDueAsync(CancellationToken.None));
        await f.Save("second");
        await f.Host.InvokeAsync<PagePdfExportView>(new RunPagePdfExport(f.Page.Id));
        Assert.True(await runner.NextDueAsync(CancellationToken.None) <= DateTime.UtcNow);
    }

    /// <summary>A formal document leaves out what the cover states about the revision; the choice is saved with the export,
    /// kept by a save that does not send it, and a new export is named after the page.</summary>
    [PostgreSqlFact]
    public async Task Cover_details_are_saved_with_the_export_and_reach_the_renderer()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var fresh = await f.Host.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(f.Page.Id));
        Assert.Equal("Guide.pdf", fresh.Settings.FileName);
        Assert.Equal(PdfCoverDetails.All, fresh.Settings.CoverDetails);
        var saved = await f.Host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(f.Page.Id,
            new(true, f.ProfileId, "guide", "document.pdf", false, false, 0, ["revisionDate", "revision"]), "https://atlas.test/"));
        Assert.Equal(["revision", "revisionDate"], saved.Settings.CoverDetails);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(["revision", "revisionDate"], f.Pdf.CoverDetails);
        // Without a configured public origin the stored one (a Host header's) is not printed: Atlas links stay relative.
        Assert.Equal("", f.Pdf.SourceOrigin);
        // An older client that does not know the choice keeps it.
        var kept = await f.SaveRule(false, saved.Settings.Version);
        Assert.Equal(["revision", "revisionDate"], kept.Settings.CoverDetails);
        await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(f.Page.Id,
            new(true, f.ProfileId, "guide", "document.pdf", false, false, kept.Settings.Version, ["secret"]), "https://atlas.test/")));
    }

    /// <summary>The PDF goes to storage the wiki does not guard: a page link names only a page the rule's owner reads, by its current
    /// title, never the title or id its author stored for a page the owner cannot open (GAP1-2).</summary>
    [PostgreSqlFact]
    public async Task A_page_link_names_only_a_page_the_rule_owner_reads()
    {
        await using var f = await Fixture.Create();
        var other = await f.Host.SeedUserAsync("other");
        var closed = await f.Host.SeedSpaceAsync("Vedení", (other, SpaceRole.Writer));
        f.Host.Actor.Current = other;
        var hidden = await f.Host.InvokeAsync<PageDto>(new CreatePage(new(closed, null, "Propouštění Q4", ContentExportFixture.Rich("x"), "x")));
        f.Host.Actor.Current = f.Actor;
        var readable = await f.Host.InvokeAsync<PageDto>(new CreatePage(new(f.SpaceId, null, "Kontakty", ContentExportFixture.Rich("x"), "x")));
        static System.Text.Json.Nodes.JsonObject Link(Guid id, string title) => new()
        {
            ["type"] = "pageReference", ["attrs"] = new System.Text.Json.Nodes.JsonObject { ["id"] = id.ToString(), ["title"] = title, ["url"] = $"#/pages/{id}" }
        };
        var content = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "atlas-rich", ["version"] = 1,
            ["document"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "doc", ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "paragraph", ["content"] = new System.Text.Json.Nodes.JsonArray(Link(readable.Id, "Starý název"), Link(hidden.Id, "Propouštění Q4"))
            }) },
            ["plugins"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "pages", ["version"] = 1 })
        }.ToJsonString();
        await f.Host.InvokeAsync<PageDto>(new SavePage(f.Page.Id, new("Guide", content, "Odkazy", f.Page.Version)));
        await f.SaveRule();

        await f.Run(DateTime.UtcNow.AddSeconds(31));

        var exported = f.Pdf.ContentJson!;
        Assert.Contains("Kontakty", exported, StringComparison.Ordinal);
        Assert.Contains("Odkaz na stránku", exported, StringComparison.Ordinal);
        foreach (var leaked in new[] { "Starý název", "Propouštění Q4", hidden.Id.ToString() })
            Assert.DoesNotContain(leaked, exported, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Failed_render_preserves_previous_pdf_and_revocation_prevents_retry()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        var previous = await File.ReadAllTextAsync(f.FileOf("document.pdf"));
        await f.Save("changed");
        f.Pdf.Fail = true;
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(previous, await File.ReadAllTextAsync(f.FileOf("document.pdf")));
        var state = await f.Host.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(f.Page.Id));
        Assert.Equal("error", state.Status);
        Assert.NotEmpty(state.LastError);
        var calls = f.Pdf.Calls;
        await f.Host.ExecuteAsync("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Actor.Id);
        f.Pdf.Fail = false;
        await f.Run(DateTime.UtcNow.AddMinutes(10));
        Assert.Equal(calls, f.Pdf.Calls);
        Assert.Equal(previous, await File.ReadAllTextAsync(f.FileOf("document.pdf")));
    }

    [PostgreSqlFact]
    public async Task Save_during_render_is_not_blocked_and_obsolete_pdf_is_not_published()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pdf.BeforeRender = async () => { started.TrySetResult(); await release.Task; };
        var running = f.Run(DateTime.UtcNow.AddSeconds(31));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Save("new while rendering").WaitAsync(TimeSpan.FromSeconds(5));
            // A second scheduler cannot publish concurrently with the first, even though saves remain free.
            await f.Run(DateTime.UtcNow.AddSeconds(31)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, f.Pdf.Calls);
        }
        finally { release.TrySetResult(); await running; }
        Assert.False(File.Exists(f.FileOf("document.pdf")));
        f.Pdf.BeforeRender = null;
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.True(File.Exists(f.FileOf("document.pdf")));
        Assert.Equal(2, f.Pdf.Calls);
    }

    [PostgreSqlFact]
    public async Task A_busy_renderer_leaves_the_export_due_without_an_error()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        // The slot is taken (another export prints or an interactive one waits): nothing is built or rendered, the rule stays due.
        f.Pdf.Busy = true;
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal((0, 0), (f.Pdf.Reservations, f.Pdf.Calls));
        Assert.Equal(("", 0), await f.Failure());
        // A renderer that still answers pdf_busy: not this export's failure either.
        f.Pdf.Busy = false;
        f.Pdf.BusyOnRender = true;
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(1, f.Pdf.Calls);
        Assert.Equal(("", 0), await f.Failure());
        f.Pdf.BusyOnRender = false;
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal((2, 2), (f.Pdf.Reservations, f.Pdf.Calls));
        Assert.True(File.Exists(f.FileOf("document.pdf")));
    }

    [PostgreSqlFact]
    public async Task Changed_storage_profile_reexports_without_a_page_edit()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        var service = f.Host.Services.GetRequiredService<StorageProfileService>();
        var profile = await service.GetAsync(f.ProfileId, CancellationToken.None);
        await service.SaveAsync(profile.Id, new(profile.Name + " updated", profile.Destination, profile.Username, profile.Domain, null,
            profile.Enabled, profile.AllowPageExports, profile.AllowBackups, profile.AllSpaces, profile.SpaceIds.ToArray(), profile.Version), CancellationToken.None);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(2, f.Pdf.Calls);
    }

    [PostgreSqlFact]
    public async Task Disabled_rule_cannot_reserve_an_unavailable_profile()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var service = f.Host.Services.GetRequiredService<StorageProfileService>();
        var unavailable = await service.SaveAsync(null, new("Private", Path.Combine(Path.GetTempPath(), "private-pdf-" + Guid.NewGuid().ToString("N")), "", "", null,
            true, true, true, false, []), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(f.Page.Id,
            new(false, unavailable.Id, "guide", "document.pdf", false, false, 0), "https://atlas.test/")));
        Assert.Equal(403, failure.Status);
    }

    [PostgreSqlFact]
    public async Task Manual_export_does_not_skip_debounce_for_a_later_saved_revision()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        await f.Host.InvokeAsync<PagePdfExportView>(new RunPagePdfExport(f.Page.Id));
        await f.Save("saved after manual request");
        await f.Run(DateTime.UtcNow);
        Assert.Equal(0, f.Pdf.Calls);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(1, f.Pdf.Calls);
    }

    /// <summary>Two handbooks export into one folder under their own names; each page keeps only its latest PDF there, a rename
    /// removes the page's previous file, and a name another page's export uses or last wrote is refused.</summary>
    [PostgreSqlFact]
    public async Task Pages_share_a_folder_and_each_keeps_only_its_latest_pdf()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var other = await f.Host.InvokeAsync<PageDto>(new CreatePage(new(f.SpaceId, null, "Second", ContentExportFixture.Rich("other"), "other")));
        await f.SaveRuleFor(f.Page.Id, "guide", "a.pdf");
        var b = await f.SaveRuleFor(other.Id, "guide", "b.pdf");
        var clash = await Assert.ThrowsAsync<ApiException>(() => f.SaveRuleFor(other.Id, "GUIDE", " A.PDF ", b.Settings.Version));
        Assert.Equal(409, clash.Status);
        Assert.Contains("jiné stránky", clash.Message);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(["a.pdf", "b.pdf"], f.Files());
        var bContent = await File.ReadAllTextAsync(f.FileOf("b.pdf"));

        await f.Save("second");
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(["a.pdf", "b.pdf"], f.Files());
        var current = await f.Host.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(f.Page.Id));
        Assert.Equal(current.LastRevisionId!.Value.ToString(), await File.ReadAllTextAsync(f.FileOf("a.pdf")));
        Assert.Equal(bContent, await File.ReadAllTextAsync(f.FileOf("b.pdf")));

        await f.SaveRuleFor(f.Page.Id, "guide", "a-renamed.pdf", current.Settings.Version);
        // The previous file still exists until the export replaces it, so its name stays the page's.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.SaveRuleFor(other.Id, "guide", "a.pdf", b.Settings.Version))).Status);
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(["a-renamed.pdf", "b.pdf"], f.Files());
        Assert.Equal(bContent, await File.ReadAllTextAsync(f.FileOf("b.pdf")));
        await f.SaveRuleFor(other.Id, "guide", "a.pdf", b.Settings.Version);
    }

    /// <summary>The file is named after the export: "Name 2026-11-08 14-30.pdf" (Prague time). Overwrite leaves only the newest file of the
    /// page, also replacing one an earlier version wrote without a date; keeping versions adds a file per export. The view names the file.</summary>
    [PostgreSqlFact]
    public async Task Exported_files_carry_the_export_date_and_only_the_newest_stays_unless_versions_are_kept()
    {
        Assert.Equal("Guide 2026-11-08 14-30.pdf", PagePdfExportRunner.DatedName("Guide.pdf", new DateTime(2026, 11, 8, 13, 30, 40, DateTimeKind.Utc)));
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var rule = await f.SaveRule();
        // An earlier version's undated file, in the folder that export created.
        Directory.CreateDirectory(f.Target);
        await File.WriteAllTextAsync(Path.Combine(f.Target, "document.pdf"), "legacy");
        await f.SetLast(f.Page.Id, "guide", "document.pdf");
        var start = DateTime.UtcNow.AddMinutes(10);
        await f.Run(start);
        var first = PagePdfExportRunner.DatedName("document.pdf", start);
        Assert.Equal([first], Directory.GetFiles(f.Target).Select(file => Path.GetFileName(file)).ToArray());
        Assert.Equal(first, (await f.Host.InvokeAsync<PagePdfExportView>(new GetPagePdfExport(f.Page.Id))).LastFileName);

        await f.Save("second");
        await f.Run(start.AddMinutes(2));
        Assert.Equal([PagePdfExportRunner.DatedName("document.pdf", start.AddMinutes(2))], Directory.GetFiles(f.Target).Select(file => Path.GetFileName(file)).ToArray());

        await f.SaveRule(true, rule.Settings.Version);
        await f.Run(start.AddMinutes(4));
        await f.Save("third");
        await f.Run(start.AddMinutes(6));
        Assert.Equal(3, Directory.GetFiles(f.Target).Length);
    }

    /// <summary>3.6 repair on open: the automatic export names its revision, so it converts a page still in the requirement block shape
    /// itself, and the renderer is handed the converted document.</summary>
    [PostgreSqlFact]
    public async Task An_export_of_a_page_in_the_old_requirement_shape_prints_the_converted_page()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        await f.SaveRule();
        var old = Pages.RequirementHeadingConverterTests.Envelope(Pages.RequirementHeadingConverterTests.Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601", "jeden", "Jeden požadavek"));
        await Events.OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""UPDATE "PageRevision" SET "ContentJson"='{old}' WHERE "PageId"='{f.Page.Id}' AND "ActiveTo" IS NULL""");
        await f.Run(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(1, f.Pdf.Calls);
        Assert.DoesNotContain(Atlas.Server.Features.Pages.Domain.RequirementPageConversion.Marker, f.Pdf.ContentJson!, StringComparison.Ordinal);
        Assert.Equal(["jeden"], Pages.RequirementsRepairOnOpenTests.RequirementHeadingIds(f.Pdf.ContentJson!));
    }

    /// <summary>Rows that predate the rule (or a race) cannot make an export replace or remove another page's PDF.</summary>
    [PostgreSqlFact]
    public async Task Export_never_replaces_or_removes_a_pdf_of_another_page()
    {
        await using var f = await Fixture.Create();
        f.Host.Actor.Current = f.Actor;
        var other = await f.Host.InvokeAsync<PageDto>(new CreatePage(new(f.SpaceId, null, "Second", ContentExportFixture.Rich("other"), "other")));
        await f.SaveRuleFor(f.Page.Id, "guide", "a.pdf");
        // Both exports of the page run in one minute (at, at + 1 s), so the second writes the first's dated name however long the steps
        // between take; across a minute boundary it would leave a second dated file of the page (DatedName). Later runs follow at in time.
        var at = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(5);
        await f.Run(at);
        // Off, so only the page exports in the next tick; a switched-off rule still claims its name. An enabled one was due too and,
        // ordered by NextAttemptAt, ran after the page whenever the run above took over a second, then rightly wrote its own b.pdf.
        var b = await f.Host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(other.Id, new(false, f.ProfileId, "guide", "b.pdf", false, false, 0), "https://atlas.test/"));
        await File.WriteAllTextAsync(Path.Combine(f.Target, "b.pdf"), "B");
        // The page's last file is the other page's current one: the next export keeps it.
        await f.SetLast(f.Page.Id, "guide", "b.pdf");
        await f.Save("second");
        await f.Run(at.AddSeconds(1));
        Assert.Equal(["a.pdf", "b.pdf"], f.Files());
        Assert.Equal("B", await File.ReadAllTextAsync(f.FileOf("b.pdf")));
        Assert.Equal(("", 0), await f.Failure());

        // The other page last wrote the page's current name: the page does not overwrite it and says why.
        await f.Host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(other.Id, new(false, f.ProfileId, "guide", "b.pdf", false, false, b.Settings.Version), "https://atlas.test/"));
        await f.SetLast(other.Id, "guide", "a.pdf");
        await File.WriteAllTextAsync(Path.Combine(f.Target, "a.pdf"), "B");
        await f.Save("third");
        await f.Run(at.AddMinutes(1));
        Assert.Equal("B", await File.ReadAllTextAsync(f.FileOf("a.pdf")));
        Assert.Contains("jiné stránky", (await f.Failure()).Error);
    }

    private static readonly System.Text.RegularExpressions.Regex DateSuffix = new(@" \d{4}-\d\d-\d\d \d\d-\d\d\.pdf$");

    private sealed class Fixture(AtlasTestHost host, string root, RecordingPdf pdf) : IAsyncDisposable
    {
        public AtlasTestHost Host => host;
        public RecordingPdf Pdf => pdf;
        public string Target => Path.Combine(root, "guide");
        public UserAccount Actor { get; private set; } = null!;
        public PageDto Page { get; private set; } = null!;
        public Guid ProfileId { get; private set; }
        public Guid SpaceId { get; private set; }
        public static async Task<Fixture> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "atlas-auto-pdf-" + Guid.NewGuid().ToString("N"));
            var pdf = new RecordingPdf();
            // Own host: the runner exports every due rule of the installation and NextDueAsync reads the whole PagePdfExport table.
            var host = await AtlasTestHost.CreateAsync("atlas_pdf_auto_", services =>
            {
                services.AddDataProtection();
                services.AddStorageProfiles();
                services.AddContentExport();
                services.AddPagePdfExports(false);
                services.AddSingleton<IContentPdfRenderer>(pdf);
            });
            var f = new Fixture(host, root, pdf);
            f.Actor = await host.SeedUserAsync("writer");
            host.Actor.Current = f.Actor;
            var space = f.SpaceId = await host.SeedSpaceAsync("Guides", (f.Actor, SpaceRole.Writer));
            f.Page = await host.InvokeAsync<PageDto>(new CreatePage(new(space, null, "Guide", ContentExportFixture.Rich("first"), "first")));
            var profile = await host.Services.GetRequiredService<StorageProfileService>().SaveAsync(null, new("Guides", root, "", "", null, true, true, true, false, [space]), CancellationToken.None);
            f.ProfileId = profile.Id;
            return f;
        }
        public Task<PagePdfExportView> SaveRule(bool history = false, int version = 0) => host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(Page.Id, new(true, ProfileId, "guide", "document.pdf", history, false, version), "https://atlas.test/"));
        public Task<PagePdfExportView> SaveRuleFor(Guid pageId, string folder, string name, int version = 0)
            => host.InvokeAsync<PagePdfExportView>(new SavePagePdfExport(pageId, new(true, ProfileId, folder, name, false, false, version), "https://atlas.test/"));
        /// <summary>The names in the folder without the export's date and time ("a 2026-11-08 14-30.pdf" is "a.pdf").</summary>
        public string[] Files() => Directory.GetFiles(Target).Select(file => DateSuffix.Replace(Path.GetFileName(file), ".pdf")).Order(StringComparer.Ordinal).ToArray();
        /// <summary>The file of that name or, when the export dated it, the dated one; a path that does not exist when nothing was written yet.</summary>
        public string FileOf(string name) => !Directory.Exists(Target) ? Path.Combine(Target, name) : Directory.GetFiles(Target).FirstOrDefault(file => Path.GetFileName(file) == name)
            ?? Directory.GetFiles(Target).FirstOrDefault(file => DateSuffix.Replace(Path.GetFileName(file), ".pdf") == name) ?? Path.Combine(Target, name);
        public async Task SetLast(Guid pageId, string folder, string name)
        {
            using var session = host.OpenSession();
            using var tx = session.BeginTransaction();
            var rule = await session.GetAsync<PagePdfExport>(pageId);
            (rule.LastStorageProfileId, rule.LastFolder, rule.LastFileName) = (ProfileId, folder, name);
            await tx.CommitAsync();
        }
        public async Task Save(string text) => Page = await host.InvokeAsync<PageDto>(new SavePage(Page.Id, new("Guide", ContentExportFixture.Rich(text), text, Page.Version)));
        public async Task<(string Error, int Failures)> Failure()
        {
            using var session = host.OpenSession();
            var rule = await session.GetAsync<PagePdfExport>(Page.Id);
            return (rule.LastError, rule.FailureCount);
        }
        public Task Run(DateTime now) => host.Services.GetRequiredService<PagePdfExportRunner>().RunDueAsync(now, CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class RecordingPdf : IContentPdfRenderer
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool BusyOnRender { get; set; }
        public bool Busy { get; set; }
        public int Reservations { get; private set; }
        public Func<Task>? BeforeRender { get; set; }
        public IReadOnlyList<string>? CoverDetails { get; private set; }
        public string? SourceOrigin { get; private set; }
        public string? ContentJson { get; private set; }
        public async Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct)
        {
            Calls++;
            CoverDetails = coverDetails;
            SourceOrigin = sourceOrigin;
            using (var zip = new System.IO.Compression.ZipArchive(archive.Stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
            using (var reader = new StreamReader(zip.Entries.Single(entry => entry.FullName.EndsWith("/content.json", StringComparison.Ordinal)).Open()))
                ContentJson = await reader.ReadToEndAsync(ct);
            archive.Stream.Position = 0;
            if (BeforeRender is not null) await BeforeRender();
            if (Fail) throw new ApiException(422, "Netisknutelný obsah.");
            if (BusyOnRender) throw new ApiException(503, "Obsazeno.", ContentPdfRenderer.BusyCode);
            return archive with { Stream = new MemoryStream(Encoding.UTF8.GetBytes(archive.Selected.Single().RevisionId.ToString())), Name = "document.pdf" };
        }
        public IContentPdfSlot? TryReserveAutomatic()
        {
            if (Busy) return null;
            Reservations++;
            return new Slot(this);
        }
        private sealed class Slot(RecordingPdf pdf) : IContentPdfSlot
        {
            public Task<ContentExportArchive> RenderAsync(ContentExportArchive archive, string sourceOrigin, IReadOnlyList<string> coverDetails, CancellationToken ct) => pdf.RenderAsync(archive, sourceOrigin, coverDetails, ct);
            public void Dispose() { }
        }
    }
}
