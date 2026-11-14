using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ContentImport.Domain;
using Xunit;
namespace Atlas.Server.Tests;

public class ContentImportFilesTests
{
    [Fact]
    public async Task Existing_archive_collision_never_deletes_original_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas_import_files_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().Build();
        var files = new ContentImportFiles(config, new TestEnvironment(root)); var id = Guid.NewGuid();
        try
        {
            await files.WriteAsync(id, new MemoryStream(new byte[] { 1, 2, 3 }), default);
            await Assert.ThrowsAsync<IOException>(() => files.WriteAsync(id, new MemoryStream(new byte[] { 9 }), default));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(files.PathFor(id)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Compression_bombs_and_symlinks_are_rejected_before_parsing()
    {
        using var input = new MemoryStream();
        using (var zip = new ZipArchive(input, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("large.md", CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open()); writer.Write(new string('a', 2_000_000));
        }
        input.Position = 0;
        Assert.Equal(413, (await Assert.ThrowsAsync<ApiException>(() => WikiJsArchive.ReadAsync(input, new("source", "Import")))).Status);
        using var link = new MemoryStream();
        using (var zip = new ZipArchive(link, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("link.md"); entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("/etc/passwd");
        }
        link.Position = 0;
        var linked = await WikiJsArchive.ReadAsync(link, new("source", "Import"));
        Assert.Empty(linked.Files);
        Assert.Contains("symbolick", linked.Excluded["link.md"], StringComparison.Ordinal);
    }
    [Fact]
    public async Task Yaml_aliases_and_invalid_utf8_never_expand_or_silently_replace_content()
    {
        // The page is left out with its reason instead of failing the export; its original stays in the ZIP.
        using var alias = ContentImportTests.Zip(("a.md", "---\ntitle: &x hello\ndescription: *x\n---\nBody"));
        var aliased = await WikiJsArchive.ReadAsync(alias, new("source", "Import"));
        Assert.Empty(aliased.Pages);
        Assert.Contains("YAML", aliased.Skipped["a.md"], StringComparison.Ordinal);
        using var input = new MemoryStream();
        using (var zip = new ZipArchive(input, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("a.md"); using var stream = entry.Open(); stream.Write(new byte[] { 0xFF, 0xFE, 0xFF });
        }
        input.Position = 0;
        var invalid = await WikiJsArchive.ReadAsync(input, new("source", "Import"));
        Assert.Empty(invalid.Pages);
        Assert.Contains("UTF-8", invalid.Skipped["a.md"], StringComparison.Ordinal);
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Atlas.Tests"; public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot"); public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
