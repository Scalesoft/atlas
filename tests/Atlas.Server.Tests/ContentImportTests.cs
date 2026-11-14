using Xunit;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Atlas.Server;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Tests.ContentImport;
namespace Atlas.Server.Tests;

public class ContentImportTests
{
    public static MemoryStream Zip(params (string Path, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, text) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
            }
        stream.Position = 0; return stream;
    }
    [Fact]
    public async Task Wiki_metadata_unquoted_colon_is_preserved_and_reported()
    {
        using var zip = Zip(("wiki/en/test.md", "---\ntitle: API: C#\npublished: true\ntags: test\n---\n\n# Hello\nWorld"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("wiki", "Import", "wiki")));
        var page = Assert.Single(source.Pages); Assert.Equal("API: C#", page.Title); Assert.Equal("en/test.md", page.Path);
        Assert.Contains(page.Findings, x => x.Construct == "front-matter-invalid" && x.Message.Contains("YAML")); Assert.DoesNotContain("published:", page.Markdown);
        Assert.Equal(["test"], page.Tags);
    }
    [Fact]
    public void Converter_preserves_structure_marks_safe_links_and_stable_heading_identity()
    {
        var page = ImportPages.Page("en/hello.md", "# Heading\n\nA **bold** [link](other.md)\n\n- First\n- Second\n\n```csharp\nvar a = 1;\n```");
        var other = ImportPages.Page("en/other.md", "Other");
        var result = ImportPages.Convert(page, [other]);
        using var json = JsonDocument.Parse(result.ContentJson);
        var content = json.RootElement.GetProperty("document").GetProperty("content");
        Assert.Equal("heading", content[0].GetProperty("type").GetString());
        Assert.NotEmpty(content[0].GetProperty("attrs").GetProperty("headingId").GetString()!);
        var href = ImportPages.Href("en/other.md");
        Assert.Contains("bold", result.ContentJson); Assert.Contains(href, result.ContentJson); Assert.Contains(href, result.Markdown);
        Assert.Contains("bulletList", result.ContentJson); Assert.Contains("codeBlock", result.ContentJson);
        Assert.Equal(result.ContentJson, ImportPages.Convert(page, [other]).ContentJson);
        Rules.ValidateDocument(result.ContentJson, result.Markdown);
    }
    [Fact]
    public void Converter_does_not_execute_html_javascript_or_fetch_remote_images()
    {
        var result = ImportPages.Convert("<script>alert(1)</script>\n\n[bad](javascript:alert)\n\n![remote](https://example.com/private.png)");
        Assert.DoesNotContain("\"href\":\"javascript:", result.ContentJson);
        Assert.DoesNotContain("\"src\":\"https:", result.ContentJson);
        Assert.DoesNotContain("alert(1)", result.ContentJson);
        Assert.Contains(result.Findings, x => x.Construct == "html-dropped:script" && x.Impact == WikiImportFinding.Lost);
        Assert.Contains(result.Findings, x => x.Construct == "link-unresolved" && x.Excerpt == "javascript:alert");
        Assert.Contains(result.Findings, x => x.Construct == "image-unavailable" && x.Excerpt == "https://example.com/private.png");
    }
    [Fact]
    public void Markdown_projection_rewrites_inline_reference_and_attachment_links_without_touching_code()
    {
        var page = ImportPages.Page("a.md", "[Inline](other.md) [Reference][other] [Attachment](file.txt)\n\n[other]: other.md\n\n```text\n[Code](other.md)\n```");
        var result = ImportPages.Convert(page, [ImportPages.Page("other.md", "Other")], ("file.txt", "text"u8.ToArray()));
        var attachment = System.Text.RegularExpressions.Regex.Match(result.ContentJson, "/api/attachments/[0-9a-f-]{36}/download").Value;
        Assert.NotEmpty(attachment); Assert.Contains(attachment, result.Markdown);
        var parsed = Markdig.Markdown.Parse(result.Markdown);
        var links = Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(parsed).ToArray();
        Assert.Equal(3, links.Length);
        Assert.Equal(2, links.Count(x => x.Url == ImportPages.Href("other.md")));
        Assert.Contains("[Code](other.md)", result.Markdown);
    }
    [Fact]
    public async Task Oversized_markdown_page_is_reported_without_failing_the_import()
    {
        using var zip = Zip(("huge.md", new string('a', WikiJsArchive.MaximumPageBytes + 1)), ("small.md", "ok"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import")));
        Assert.Equal("small.md", Assert.Single(source.Pages).Path);
        Assert.Contains("1 MB", source.Skipped["huge.md"], StringComparison.Ordinal);
        var report = ImportReportBuilder.Build(Guid.NewGuid(), "token", DateTime.UtcNow, null, "Import", source, []);
        Assert.Equal(("unsupported", "preserve"), (report.Items.Single(x => x.Path == "huge.md").Kind, report.Items.Single(x => x.Path == "huge.md").Action));
    }
    [Fact]
    public async Task Html_page_becomes_a_page_at_the_markdown_path()
    {
        using var zip = Zip(("Corporate/employees.html", "<!--\ntitle: Zaměstnanci\npublished: true\n-->\n<h1>Zaměstnanci</h1><p>První <strong>řádek</strong>.</p>"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import")));
        var page = Assert.Single(source.Pages);
        Assert.Equal("Corporate/employees.md", page.Path);
        Assert.Equal("Corporate/employees.html", page.SourceFilePath);
        Assert.Equal("Zaměstnanci", page.Title);
        Assert.Equal("# Zaměstnanci\n\nPrvní **řádek**.", page.Markdown);
        // The H1 repeats the metadata title, so the converter omits it; its lines are not source lines.
        Assert.True(page.DropLeadingHeading); Assert.True(page.SourceIsHtml);
    }
    [Fact]
    public async Task Markdown_file_wins_over_its_html_sibling()
    {
        using var zip = Zip(("a.md", "---\ntitle: Z markdownu\n---\n\n# Markdown"), ("a.html", "<!--\ntitle: Z HTML\n-->\n<p>HTML</p>"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import")));
        var page = Assert.Single(source.Pages);
        Assert.Equal("Z markdownu", page.Title);
        Assert.Equal("a.md", page.SourceFilePath);
        Assert.Contains("a.html", source.Skipped.Keys);
        Assert.Contains("Markdown", source.Skipped["a.html"], StringComparison.Ordinal);
    }
    [Fact]
    public async Task Html_page_without_convertible_content_is_reported_instead_of_created()
    {
        using var zip = Zip(("widget.html", "<!--\ntitle: Widget\n-->\n<form><input value=\"a\"><button>Odeslat</button></form>"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import")));
        Assert.Empty(source.Pages);
        Assert.Contains("widget.html", source.Skipped.Keys);
    }
    [Fact]
    public async Task Oversized_converted_html_is_reported_without_failing_the_import()
    {
        var big = string.Concat(Enumerable.Repeat("<p>dostatečně dlouhý odstavec pro překročení limitu</p>", 30_000));
        using var zip = Zip(("huge.html", "<!--\ntitle: Velká\n-->\n" + big), ("small.html", "<p>ok</p>"));
        var source = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new("source", "Import")));
        Assert.Equal("small.md", Assert.Single(source.Pages).Path);
        Assert.Contains("1 MB", source.Skipped["huge.html"], StringComparison.Ordinal);
    }
    [Fact]
    public void Report_does_not_list_a_converted_html_file_as_unsupported()
    {
        var page = new WikiSourcePage("a.md", "A", "# A", "hash", [], "a.html");
        var document = new ImportedDocument("{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}}", "# A", []);
        var item = new WikiImportItem("a.md", "page", "create", "A", Guid.NewGuid(), null, "hash", []);
        var archive = new WikiSourceArchive(
            new Dictionary<string, WikiSourceFile> { ["a.html"] = new("a.html", [1], "hash") },
            [page], new Dictionary<string, string>());
        var report = ImportReportBuilder.Build(Guid.NewGuid(), "token", DateTime.UtcNow, null, "Import", archive,
            [new ImportPlan(page, document, [], item)]);
        Assert.Equal(0, report.Counts.Unsupported);
        Assert.Equal("page", Assert.Single(report.Items).Kind);
    }
}
