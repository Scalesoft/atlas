using System.IO.Compression;
using System.Text;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Xunit;

namespace Atlas.Server.Tests.ContentImport;

/// <summary>
/// Real exports (a Wiki.js git storage repository zipped on macOS or downloaded from GitHub) contain
/// files a page import cannot take as they are. One such file is reported, never a reason to reject the export.
/// </summary>
public class WikiArchiveRobustnessTests
{
    private static readonly WikiImportOptions Options = WikiJsArchive.Validate(new("source", "Import"));

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("/absolute.md")]
    [InlineData("C:/secret.md")]
    [InlineData("a/../secret.md")]
    [InlineData("a\\secret.md")]
    public async Task Unsafe_path_is_reported_and_the_rest_imported(string path)
    {
        using var zip = ContentImportTests.Zip((path, "hello"), ("ok.md", "ok"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal("ok.md", Assert.Single(archive.Pages).Path);
        Assert.Equal(["ok.md"], archive.Files.Keys);
        var item = Report(archive).Items.Single(x => x.Path == path);
        Assert.Equal(("unsupported", "preserve"), (item.Kind, item.Action));
        Assert.Contains(item.Warnings, x => x.Contains("cest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Symbolic_link_is_reported_and_the_rest_imported()
    {
        using var zip = Build(("link.md", "target.md"u8.ToArray(), true), ("ok.md", "ok"u8.ToArray(), false));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal("ok.md", Assert.Single(archive.Pages).Path);
        Assert.Contains(Report(archive).Items, x => x.Path == "link.md" && x.Kind == "unsupported" && x.Warnings.Any(w => w.Contains("symbolick", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Pages_differing_only_in_letter_case_are_both_imported()
    {
        using var zip = ContentImportTests.Zip(("Docs/Docker.html", "<!--\ntitle: Docker\n-->\n<p>Nasazení</p>"),
            ("Docs/docker.html", "<!--\ntitle: Docker 2025\n-->\n<p>Viz <a href=\"Docker\">Docker</a></p>"),
            ("Docs/index.md", "[velká](Docker) [malá](docker)"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal(["Docs/Docker.md", "Docs/docker-2.md", "Docs/index.md"], archive.Pages.Select(x => x.Path).Order(StringComparer.Ordinal));
        var renamed = archive.Pages.Single(x => x.Path == "Docs/docker-2.md");
        Assert.Equal(("Docker 2025", "Docs/docker-2.html"), (renamed.Title, renamed.SourceFilePath));
        Assert.Contains(renamed.Warnings, x => x.Contains("Docs/docker.html", StringComparison.Ordinal) && x.Contains("velikost", StringComparison.OrdinalIgnoreCase));
        // A link spelled exactly like the renamed file reaches it; the other spelling keeps its page.
        var sources = ImportPlanner.Sources(archive);
        var (pages, unsupported) = ImportPlanner.Convert(archive, sources, sources.ToDictionary(x => x.Path, x => ImportPages.Id(x.Path), StringComparer.OrdinalIgnoreCase), Options);
        Assert.Empty(unsupported);
        var index = pages.Single(x => x.Source.Path == "Docs/index.md").Document.ContentJson;
        Assert.Equal(1, CountOf(index, ImportPages.Href("Docs/Docker.md")));
        Assert.Equal(1, CountOf(index, ImportPages.Href("Docs/docker-2.md")));
    }

    [Fact]
    public async Task Renamed_name_never_collides_with_another_entry()
    {
        using var zip = ContentImportTests.Zip(("A.md", "one"), ("a.md", "two"), ("a-2.md", "three"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal(["A.md", "a-2.md", "a-3.md"], archive.Pages.Select(x => x.Path).Order(StringComparer.Ordinal));
        Assert.Equal("two", archive.Pages.Single(x => x.Path == "a-3.md").Markdown);
    }

    [Fact]
    public async Task Decomposed_unicode_name_from_macos_is_normalised()
    {
        using var zip = ContentImportTests.Zip(("w9_mrac\u030cena.xml", "<x/>"), ("mrac\u030cena.md", "text"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal("mračena.md", Assert.Single(archive.Pages).Path);
        Assert.Contains("w9_mračena.xml", archive.Files.Keys);
    }

    [Fact]
    public async Task Invalid_utf8_or_broken_metadata_skips_only_that_page()
    {
        using var zip = Build(("latin.md", [0x63, 0x61, 0x66, 0xE9], false), ("broken.md", "---\ntitle: nikdy neuzavřeno\n"u8.ToArray(), false), ("ok.md", "ok"u8.ToArray(), false));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal("ok.md", Assert.Single(archive.Pages).Path);
        Assert.Contains("UTF-8", archive.Skipped["latin.md"], StringComparison.Ordinal);
        Assert.Contains("metadata", archive.Skipped["broken.md"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uploaded_html_document_without_wiki_header_is_a_file_not_a_page()
    {
        using var zip = ContentImportTests.Zip(("assets/technet.html", "<!DOCTYPE html>\n<html><body><p>Uložená stránka</p></body></html>"),
            ("page.html", "<p>Fragment bez hlavičky zůstává stránkou</p>"), ("a.md", "[Technet](assets/technet.html)"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        Assert.Equal(["a.md", "page.md"], archive.Pages.Select(x => x.Path).Order(StringComparer.Ordinal));
        Assert.False(archive.Skipped.ContainsKey("assets/technet.html"));
        var sources = ImportPlanner.Sources(archive);
        var (pages, _) = ImportPlanner.Convert(archive, sources, sources.ToDictionary(x => x.Path, x => ImportPages.Id(x.Path), StringComparer.OrdinalIgnoreCase), Options);
        Assert.Equal("assets/technet.html", Assert.Single(pages.Single(x => x.Source.Path == "a.md").Assets.Values).Path);
    }

    [Fact]
    public async Task Certificates_and_keys_are_flagged_wherever_they_appear()
    {
        using var zip = ContentImportTests.Zip(("a.md", "[certifikát](keys/client.pfx)"), ("keys/client.pfx", "secret"), ("id_rsa", "key"), ("notes.txt", "plain"));
        var archive = await WikiJsArchive.ReadAsync(zip, Options);
        var sources = ImportPlanner.Sources(archive);
        var (pages, _) = ImportPlanner.Convert(archive, sources, sources.ToDictionary(x => x.Path, x => ImportPages.Id(x.Path), StringComparer.OrdinalIgnoreCase), Options);
        var plans = pages.Select(x => new ImportPlan(x.Source, x.Document, x.Assets,
            new WikiImportItem(x.Source.Path, "page", "create", x.Source.Title, ImportPages.Id(x.Source.Path), null, "hash", x.Source.Warnings))).ToList();
        var report = Report(archive, plans);
        var copied = report.Items.Single(x => x.Path == "keys/client.pfx");
        Assert.Equal(("asset", "copy"), (copied.Kind, copied.Action));
        Assert.Contains(copied.Warnings, x => x.Contains("klíč", StringComparison.Ordinal));
        Assert.Contains(report.Items.Single(x => x.Path == "a.md").Warnings, x => x.Contains("keys/client.pfx", StringComparison.Ordinal));
        Assert.Contains(report.Items.Single(x => x.Path == "id_rsa").Warnings, x => x.Contains("klíč", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Items.Single(x => x.Path == "notes.txt").Warnings, x => x.Contains("klíč", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, x => x.Contains("2", StringComparison.Ordinal) && x.Contains("klíč", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_wrapping_folder_is_detected()
    {
        using (var wrapped = ContentImportTests.Zip(("wikijs-backup/a.md", "a"), ("wikijs-backup/Projects/b.md", "b")))
        {
            Assert.Equal("wikijs-backup", WikiJsArchive.SingleRoot(wrapped));
            Assert.Equal(0, wrapped.Position);
        }
        using (var flat = ContentImportTests.Zip(("en/a.md", "a"), ("README.md", "b")))
        {
            Assert.Null(WikiJsArchive.SingleRoot(flat));
        }
    }

    [Fact]
    public async Task Stripped_folder_is_named_in_the_report()
    {
        using var zip = ContentImportTests.Zip(("wikijs-backup/a.md", "a"));
        var archive = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import", "wikijs-backup")));
        Assert.Equal("a.md", Assert.Single(archive.Pages).Path);
        Assert.Contains(Report(archive).Warnings, x => x.Contains("wikijs-backup/", StringComparison.Ordinal));
    }

    private static WikiImportReport Report(WikiSourceArchive archive, List<ImportPlan>? plans = null)
    {
        return ImportReportBuilder.Build(Guid.NewGuid(), "token", DateTime.UtcNow, null, "Import", archive, plans ?? []);
    }

    private static int CountOf(string text, string value)
    {
        return (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    }

    private static MemoryStream Build(params (string Path, byte[] Bytes, bool Symlink)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, bytes, symlink) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                if (symlink)
                {
                    entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                }
                using var output = entry.Open();
                output.Write(bytes);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
