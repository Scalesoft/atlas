using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class ContentExportHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Export_selects_authorized_revision_and_hashes_every_payload_without_private_draft_leaks()
    {
        await using var f = await ContentExportFixture.CreateAsync(nameof(ContentExportHandlerTests));
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Public title", ContentExportFixture.Rich("Public title"), "Public title")));
        var attachment = await f.UploadAsync(f.Writer, page.Id, "../../guide.html", new MemoryStream("published file"u8.ToArray()), false);
        var rich = System.Text.Json.JsonSerializer.Serialize(new { format = "atlas-rich", version = 1, plugins = new[] { new { id = "attachments", version = 1 } }, document = new { type = "doc", content = new[] { new { type = "attachment", attrs = new { data = new { version = 1, name = "guide", url = attachment.Url } } } } } });
        var first = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Public title", rich, "Public markdown", page.Version)));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(first.Version)));
        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Working title", ContentExportFixture.Rich("working"), "Working markdown", first.Version)));
        var privatePage = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Private working", ContentExportFixture.Rich("Private working"), "Private working")));
        await using (var archive = await f.As<ContentExportArchive>(f.Reader, new BuildContentExport(page.Id, false)))
        {
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
            var manifest = await Manifest(zip);
            Assert.Equal("Public title", manifest["pages"]![0]!["title"]!.GetValue<string>());
            // The projection the server wrote for the published attachment block.
            Assert.Equal("guide · " + attachment.Url, await Read(zip.GetEntry($"pages/{page.Id:D}/content.md")!));
            Assert.Single(manifest["attachments"]!.AsArray());
            Assert.All(zip.Entries, entry => Assert.DoesNotContain("..", entry.FullName));
            foreach (var file in manifest["files"]!.AsArray())
            {
                await using var stream = zip.GetEntry(file!["path"]!.GetValue<string>())!.Open();
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
                Assert.Equal(file["sha256"]!.GetValue<string>(), hash);
            }
            Assert.Equal("guide.html", manifest["attachments"]![0]!["name"]!.GetValue<string>());
            await f.As(f.Reader, new RevalidateContentExport(f.SpaceId, archive.Selected));
        }
        await using (var archive = await f.As<ContentExportArchive>(f.Reader, new BuildContentExport(f.SpaceId, true)))
        {
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
            Assert.Single((await Manifest(zip))["pages"]!.AsArray());
        }
        await using (var archive = await f.As<ContentExportArchive>(f.OtherWriter, new BuildContentExport(f.SpaceId, true)))
        {
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
            var pages = (await Manifest(zip))["pages"]!.AsArray();
            Assert.Equal("Working title", Assert.Single(pages)!["title"]!.GetValue<string>());
        }
        await using (var archive = await f.As<ContentExportArchive>(f.Writer, new BuildContentExport(f.SpaceId, true)))
        {
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
            Assert.Equal(2, (await Manifest(zip))["pages"]!.AsArray().Count);
        }
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<ContentExportArchive>(f.OtherWriter, new BuildContentExport(privatePage.Id, false)))).Status);
        Assert.Empty(Directory.GetFiles(f.ExportFiles.Root));
    }

    [PostgreSqlFact]
    public async Task Final_guard_rechecks_revocation_demotion_and_exact_reader_publication_and_corrupt_blobs_cleanup()
    {
        await using var f = await ContentExportFixture.CreateAsync(nameof(ContentExportHandlerTests));
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "One", ContentExportFixture.Rich("One"), "One")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        using var session = f.Host.Database.OpenSession();
        var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
        var selected = new[] { new ExportSelection(page.Id, f.SpaceId, revision.Id) };
        await f.As(f.Reader, new RevalidateContentExport(f.SpaceId, selected));
        page = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Republished", ContentExportFixture.Rich("republished"), "republished", page.Version)));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As(f.Reader, new RevalidateContentExport(f.SpaceId, selected)))).Status);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Reader.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As(f.Reader, new RevalidateContentExport(f.SpaceId, selected)))).Status);
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<ContentExportArchive>(f.Admin, new BuildContentExport(page.Id, false)))).Status);
        var file = await f.UploadAsync(f.Writer, page.Id, "file.txt", new MemoryStream("intact"u8.ToArray()), false);
        var rich = System.Text.Json.JsonSerializer.Serialize(new { format = "atlas-rich", version = 1, plugins = new[] { new { id = "attachments", version = 1 } }, document = new { type = "doc", content = new[] { new { type = "attachment", attrs = new { data = new { version = 1, name = "file", url = file.Url } } } } } });
        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("New", rich, "New", page.Version)));
        await File.WriteAllTextAsync(f.Storage.Files.PathFor(file.Id), "corrupted");
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<ContentExportArchive>(f.Writer, new BuildContentExport(page.Id, false)))).Status);
        Assert.Empty(Directory.GetFiles(f.ExportFiles.Root));
    }

    [PostgreSqlFact]
    public async Task Export_writes_the_published_content_json_byte_identical()
    {
        await using var f = await ContentExportFixture.CreateAsync(nameof(ContentExportHandlerTests));
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Žluťoučký \"kůň\" C:\\data"}]}]},"plugins":[]}""";
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Bytes", rich, "Bytes")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        await using var archive = await f.As<ContentExportArchive>(f.Reader, new BuildContentExport(page.Id, false));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
        await using var stream = zip.GetEntry($"pages/{page.Id:D}/content.json")!.Open();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(rich), bytes.ToArray());
    }

    [PostgreSqlFact]
    public async Task Export_carries_neither_the_tags_nor_the_icon_of_a_page()
    {
        await using var f = await ContentExportFixture.CreateAsync(nameof(ContentExportHandlerTests));
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Označená", ContentExportFixture.Rich("Text"), "Text")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        await f.As(f.Writer, new ChangePageTags(page.Id, new ChangePageTagsRequest(["štítekkexportu"], null, null)));
        await f.As(f.Writer, new SetPageDecoration(page.Id, new SetPageDecorationRequest("star", "red-2", null)));
        await using var archive = await f.As<ContentExportArchive>(f.Reader, new BuildContentExport(page.Id, false));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
        foreach (var entry in zip.Entries.Where(x => x.Name.EndsWith(".md", StringComparison.Ordinal) || x.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var text = await Read(entry);
            Assert.DoesNotContain("štítekkexportu", text, StringComparison.Ordinal);
            Assert.DoesNotContain("star", text, StringComparison.Ordinal);
            Assert.DoesNotContain("red-2", text, StringComparison.Ordinal);
        }
    }

    private static async Task<JsonNode> Manifest(ZipArchive zip)
    {
        return JsonNode.Parse(await Read(zip.GetEntry("manifest.json")!))!;
    }

    private static async Task<string> Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }
}
