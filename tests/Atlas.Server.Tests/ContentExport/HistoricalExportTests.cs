using System.IO.Compression;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class HistoricalExportTests
{
    [PostgreSqlFact]
    public async Task Explicit_revision_exports_old_content_and_requires_writer_access()
    {
        await using var f = await ContentExportFixture.CreateAsync(nameof(HistoricalExportTests));
        var first = await f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, null, "Old", ContentExportFixture.Rich("old"), "old")));
        using var session = f.Host.Database.OpenSession();
        var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == first.Id && x.ActiveTo == null).SingleOrDefaultAsync();
        await f.As<PageDto>(f.Writer, new PublishPage(first.Id, new(first.Version)));
        await f.As<PageDto>(f.Writer, new SavePage(first.Id, new("New", ContentExportFixture.Rich("new"), "new", first.Version)));
        await using var archive = await f.As<ContentExportArchive>(f.Writer, new BuildContentExport(first.Id, false, revision.Id));
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, true);
        using var reader = new StreamReader(zip.GetEntry($"pages/{first.Id:D}/content.md")!.Open());
        Assert.Equal("old", await reader.ReadToEndAsync());
        Assert.Equal(revision.Id, Assert.Single(archive.Selected).RevisionId);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<ContentExportArchive>(f.Reader, new BuildContentExport(first.Id, false, revision.Id)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<ContentExportArchive>(f.Writer, new BuildContentExport(f.SpaceId, true, revision.Id)))).Status);
    }
}
