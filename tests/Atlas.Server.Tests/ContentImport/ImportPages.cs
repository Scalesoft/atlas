using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Tests.ContentImport;

/// <summary>
/// Converts pages the way an import does (archive, heading outlines, <see cref="ImportLinkResolver"/>),
/// with page identities derived from the page path so results are repeatable.
/// </summary>
internal static class ImportPages
{
    public static byte[] Png { get; } = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "wiki-import", "github", "images", "architecture.png"));

    public static Guid Id(string path)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())).AsSpan(0, 16));
    }

    public static string Href(string path, string? headingId = null)
    {
        return ImportLinkResolver.PageHref(Id(path), headingId);
    }

    public static WikiSourcePage Page(string path, string markdown)
    {
        return new WikiSourcePage(path, Path.GetFileNameWithoutExtension(path), markdown, "hash", [], path);
    }

    /// <summary><paramref name="markdown"/> as <c>page.md</c> in an import with the given attachment files.</summary>
    public static ImportedDocument Convert(string markdown, params (string Path, byte[] Bytes)[] files)
    {
        return Convert(Page("page.md", markdown), [], files);
    }

    /// <summary><paramref name="page"/> in an import together with <paramref name="others"/> and the given files.</summary>
    public static ImportedDocument Convert(WikiSourcePage page, IReadOnlyList<WikiSourcePage> others, params (string Path, byte[] Bytes)[] files)
    {
        return ConvertAll([page, .. others], files).Pages.Single(x => x.Source.Path == page.Path).Document;
    }

    public static (List<(WikiSourcePage Source, ImportedDocument Document, Dictionary<Guid, WikiSourceFile> Assets)> Pages, IReadOnlyDictionary<string, string> Unsupported) ConvertAll(
        IReadOnlyList<WikiSourcePage> pages, params (string Path, byte[] Bytes)[] files)
    {
        var archive = new WikiSourceArchive(files.ToDictionary(x => x.Path, x => new WikiSourceFile(x.Path, x.Bytes, System.Convert.ToHexStringLower(SHA256.HashData(x.Bytes))), StringComparer.OrdinalIgnoreCase),
            pages, new Dictionary<string, string>());
        var ids = pages.ToDictionary(x => x.Path, x => Id(x.Path), StringComparer.OrdinalIgnoreCase);
        return ImportPlanner.Convert(archive, pages, ids, new WikiImportOptions("test", "Import"));
    }

    /// <summary>A fixture wiki of tests/fixtures/wiki-import read from a ZIP and converted as a whole.</summary>
    public static async Task<List<(WikiSourcePage Source, ImportedDocument Document, Dictionary<Guid, WikiSourceFile> Assets)>> FixtureAsync(string name, string? baseUrl = null)
    {
        using var zip = new MemoryStream();
        ZipFile.CreateFromDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures", "wiki-import", name), zip);
        zip.Position = 0;
        var options = WikiJsArchive.Validate(new WikiImportOptions("fixture-" + name, "Import", null, baseUrl));
        var archive = await WikiJsArchive.ReadAsync(zip, options);
        var sources = ImportPlanner.Sources(archive);
        var (pages, unsupported) = ImportPlanner.Convert(archive, sources, sources.ToDictionary(x => x.Path, x => Id(x.Path), StringComparer.OrdinalIgnoreCase), options);
        return unsupported.Count == 0 ? pages : throw new InvalidOperationException("Fixture page not converted: " + string.Join(", ", unsupported.Keys));
    }
}
