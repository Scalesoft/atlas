using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Attachments;

public sealed class AttachmentPersistenceTests
{
    [PostgreSqlFact]
    public async Task Attachment_and_revision_reference_round_trip_through_class_mappings()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_attachment_map_");
        var pages = new TestPages(host);
        var writer = await host.SeedUserAsync("writer");
        var spaceId = await host.SeedSpaceAsync("Files", (writer, SpaceRole.Writer));
        var page = await pages.CreateAsync(new CreatePageRequest(spaceId, null, "Files", Rich("Files"), "Files"), writer, default);
        var pageId = page.Id;
        var attachmentId = Guid.NewGuid();
        var revisionId = Guid.Empty;
        await host.SeedAsync(async session =>
        {
            revisionId = await session.QueryOver<PageRevision>()
                .Where(x => x.PageId == pageId && x.ActiveTo == null)
                .Select(x => x.Id)
                .SingleOrDefaultAsync<Guid>();
            await session.SaveAsync(new Attachment
            {
                Id = attachmentId,
                PageId = pageId,
                UploadedBy = writer.Id,
                Name = "notes.txt",
                MimeType = "application/octet-stream",
                Size = 5,
                Sha256 = new string('a', 64),
                IsImage = false,
                ActiveFrom = DateTime.UtcNow
            });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevisionAttachment { PageId = pageId, RevisionId = revisionId, AttachmentId = attachmentId });
        });
        using var check = host.OpenSession();
        var stored = await check.GetAsync<Attachment>(attachmentId);
        Assert.Equal(pageId, stored.PageId);
        Assert.Equal(DateTimeKind.Utc, stored.ActiveFrom.Kind);
        Assert.Null(stored.ActiveTo);
        var reference = await check.QueryOver<PageRevisionAttachment>()
            .Where(x => x.RevisionId == revisionId && x.AttachmentId == attachmentId)
            .SingleOrDefaultAsync();
        Assert.Equal(pageId, reference.PageId);
    }

    [PostgreSqlFact]
    public async Task Binding_the_same_revision_twice_inserts_each_reference_once_and_rejects_foreign_attachments()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_attachment_bind_");
        var pages = new TestPages(host);
        var writer = await host.SeedUserAsync("writer");
        var spaceId = await host.SeedSpaceAsync("Files", (writer, SpaceRole.Writer));
        var pageId = (await pages.CreateAsync(new CreatePageRequest(spaceId, null, "Files", Rich("Files"), "Files"), writer, default)).Id;
        var otherPageId = (await pages.CreateAsync(new CreatePageRequest(spaceId, null, "Other", Rich("Other"), "Other"), writer, default)).Id;
        var attachmentId = Guid.NewGuid();
        await host.SeedAsync(async session => await session.SaveAsync(new Attachment
        {
            Id = attachmentId,
            PageId = pageId,
            UploadedBy = writer.Id,
            Name = "notes.txt",
            MimeType = "application/octet-stream",
            Size = 5,
            Sha256 = new string('a', 64),
            IsImage = false,
            ActiveFrom = DateTime.UtcNow
        }));
        var json = "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"attachment\",\"attrs\":{\"data\":{\"version\":1,\"name\":\"file\",\"url\":\"/api/attachments/"
            + attachmentId.ToString("D") + "/download\"}}}]}}";
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync();
            revision.ContentJson = json;
            await AttachmentReferences.BindRevisionAsync(session, revision);
            await AttachmentReferences.BindRevisionAsync(session, revision);
            await transaction.CommitAsync();
        }
        using (var check = host.OpenSession())
        {
            Assert.Equal(1, await check.QueryOver<PageRevisionAttachment>().Where(x => x.AttachmentId == attachmentId).RowCountAsync());
        }
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var foreign = await session.QueryOver<PageRevision>().Where(x => x.PageId == otherPageId && x.ActiveTo == null).SingleOrDefaultAsync();
            foreign.ContentJson = json;
            var error = await Assert.ThrowsAsync<ApiException>(() => AttachmentReferences.BindRevisionAsync(session, foreign));
            Assert.Equal(400, error.Status);
            Assert.Equal("Příloha nepatří k této stránce nebo už není dostupná.", error.Message);
            await transaction.RollbackAsync();
        }
    }

    private static string Rich(string text)
    {
        return "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";
    }
}
