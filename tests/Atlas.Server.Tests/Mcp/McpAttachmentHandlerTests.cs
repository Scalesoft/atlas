using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Attachments;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The second transaction of an MCP upload, between whose phases the bytes are already on disk: it decides the page and G5 again
/// under the page locks, and a refusal there removes the blob it was given, so nothing written for a refused upload survives.
/// </summary>
public sealed class McpAttachmentHandlerTests
{
    [PostgreSqlFact]
    public async Task Store_rechecks_edit_rights_and_G5_after_the_write_and_removes_a_refused_blob()
    {
        await using var f = await AttachmentFixture.CreateAsync();
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Files", AttachmentFixture.Rich("Files"), "Files")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));

        var stored = await f.As<McpStoredAttachment>(f.Writer, await WrittenAsync(f, page.Id));
        Assert.Equal((page.Id, f.SpaceId), (stored.PageId, stored.SpaceId));
        Assert.True(File.Exists(f.Files.PathFor(stored.Attachment.Id)));

        // A reader of the published page (edit right lost between the phases) gets the 403 REST gives, a space switched out of MCP
        // the 404 of a missing page; either way the blob of the refused upload is gone.
        var reader = await WrittenAsync(f, page.Id);
        var readerError = await Assert.ThrowsAsync<ApiException>(() => f.As<McpStoredAttachment>(f.Reader, reader));
        await f.Execute($"""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom")
            VALUES (gen_random_uuid(), :id, false, '{f.Writer.Id}', clock_timestamp())
            """, f.SpaceId);
        var excluded = await WrittenAsync(f, page.Id);
        var excludedError = await Assert.ThrowsAsync<ApiException>(() => f.As<McpStoredAttachment>(f.Writer, excluded));
        Assert.Equal(403, readerError.Status);
        Assert.Equal((404, McpVisibility.MissingPage), (excludedError.Status, excludedError.Message));
        Assert.False(File.Exists(f.Files.PathFor(reader.Id)));
        Assert.False(File.Exists(f.Files.PathFor(excluded.Id)));
        using var session = f.Host.OpenSession();
        Assert.Equal(stored.Attachment.Id, (await session.QueryOver<Attachment>().Where(x => x.PageId == page.Id).ListAsync()).Single().Id);
    }

    /// <summary>A blob on disk and the store command for it, as the tool has them after the first transaction.</summary>
    private static async Task<StoreMcpAttachment> WrittenAsync(AttachmentFixture f, Guid pageId)
    {
        var id = Guid.NewGuid();
        var written = await f.Files.WriteAsync(id, new MemoryStream("bytes"u8.ToArray()), false, CancellationToken.None);
        return new StoreMcpAttachment(id, pageId, "notes.txt", written.Size, written.Hash, written.MimeType, false);
    }
}
