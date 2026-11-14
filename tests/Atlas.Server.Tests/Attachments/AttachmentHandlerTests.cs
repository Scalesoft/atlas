using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Xunit;

namespace Atlas.Server.Tests.Attachments;

public sealed class AttachmentHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Reader_download_tracks_exact_published_revision_and_never_current_draft_or_unbound_upload()
    {
        await using var f = await AttachmentFixture.CreateAsync();
        var page = await Create(f, "Files");
        var a = await Upload(f, page.Id, "First");
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(a.Id, false)));
        await Denied(404, () => f.As<AttachmentContentDto>(f.OtherWriter, new OpenAttachment(a.Id, false)));
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Files", Rich(a.Url), "File", page.Version)));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(saved.Version)));
        await f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(a.Id, false));
        var b = await Upload(f, page.Id, "Second");
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(b.Id, false)));
        var draft = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Draft", Rich(b.Url), "Second", saved.Version)));
        await f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(a.Id, false));
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(b.Id, false)));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(draft.Version)));
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(a.Id, false)));
        var second = await f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(b.Id, false));
        await using (var stream = f.Files.Open(second.Id))
        {
            using var text = new StreamReader(stream);
            Assert.Equal("Second", await text.ReadToEndAsync());
        }
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(b.Id, true)));
        await f.Execute("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=clock_timestamp(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", page.Id);
        await Denied(404, () => f.As<AttachmentContentDto>(f.Writer, new OpenAttachment(b.Id, false)));
        await Denied(404, () => Upload(f, page.Id, "Trashed"));
        await f.Execute("UPDATE \"Page\" SET \"IsTrashed\"=false,\"TrashedAt\"=NULL,\"TrashedById\"=NULL,\"TrashBatchId\"=NULL WHERE \"Id\"=:id", page.Id);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Reader.Id);
        await Denied(404, () => f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(b.Id, false)));
    }

    [PostgreSqlFact]
    public async Task Rename_carries_attachment_bindings_so_readers_can_open_them_after_publish()
    {
        await using var f = await AttachmentFixture.CreateAsync();
        var page = await Create(f, "Files");
        var a = await Upload(f, page.Id, "First");
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Files", Rich(a.Url), "File", page.Version)));
        await f.As(f.Writer, new RenamePage(page.Id, new RenamePageRequest("Renamed files", saved.Version)));
        var renamed = await f.As<PageDto>(f.Writer, new GetPage(page.Id));
        Assert.Equal(saved.Version + 1, renamed.Version);
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(renamed.Version)));
        await f.As<AttachmentContentDto>(f.Reader, new OpenAttachment(a.Id, false));
    }

    [PostgreSqlFact]
    public async Task Cross_page_reference_is_rejected_atomically_and_upload_permission_is_rechecked_after_streaming()
    {
        await using var f = await AttachmentFixture.CreateAsync();
        var a = await Create(f, "A");
        var b = await Create(f, "B");
        var file = await Upload(f, a.Id, "Only A");
        await Denied(400, () => f.As<PageDto>(f.Writer, new SavePage(b.Id, new SavePageRequest("B changed", Rich(file.Url), "", b.Version))));
        Assert.Equal(b.Version, (await f.As<PageDto>(f.Writer, new GetPage(b.Id))).Version);
        var revoked = Guid.NewGuid();
        await using var stream = new CallbackStream("revoked"u8.ToArray(),
            () => f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Writer.Id));
        await Denied(401, () => f.UploadAsync(f.Writer, a.Id, "revoked.txt", stream, id: revoked));
        // The bytes written before the refusal are gone again (the folder is shared, so this upload's own file).
        Assert.False(File.Exists(f.Files.PathFor(revoked)));
        using var session = f.Host.OpenSession();
        Assert.Equal(1, await session.QueryOver<Attachment>().Where(x => x.PageId == a.Id || x.PageId == b.Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task List_and_metadata_decide_as_the_download_and_tell_uploads_from_used_attachments()
    {
        await using var f = await AttachmentFixture.CreateAsync();
        var page = await Create(f, "Files");
        var published = await Upload(f, page.Id, "Published");
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Files", Rich(published.Url), "File", page.Version)));
        var publication = await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(saved.Version)));
        var draftOnly = await Upload(f, page.Id, "Draft");
        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Files", Rich(draftOnly.Url), "Draft", publication.Version)));
        var unused = await Upload(f, page.Id, "Unused");

        // An editor sees every active upload, each marked with the revisions that reference it.
        var editor = await f.As<PageAttachmentsDto>(f.Writer, new ListPageAttachments(page.Id));
        Assert.Equal([(published.Id, true, (bool?)false), (draftOnly.Id, false, true), (unused.Id, false, false)],
            editor.Items.Select(x => (x.Id, x.UsedInPublished, x.UsedInWorking)).ToArray());
        Assert.True((await f.As<AttachmentInfoDto>(f.Writer, new GetAttachmentInfo(draftOnly.Id))).UsedInWorking);
        // A reader sees only what the publication references, and nothing about the working revision; the rest is the download's 404.
        var reader = Assert.Single((await f.As<PageAttachmentsDto>(f.Reader, new ListPageAttachments(page.Id))).Items);
        Assert.Equal((published.Id, "notes.txt", 9L, true, (bool?)null), (reader.Id, reader.Name, reader.Size, reader.UsedInPublished, reader.UsedInWorking));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("Published"u8)), reader.Sha256);
        await Denied(404, () => f.As<AttachmentInfoDto>(f.Reader, new GetAttachmentInfo(draftOnly.Id)));
        await Denied(404, () => f.As<AttachmentInfoDto>(f.Reader, new GetAttachmentInfo(unused.Id)));
        // A never published page is the reader's 404, not an empty list.
        var draftPage = await Create(f, "Draft page");
        await Denied(404, () => f.As<PageAttachmentsDto>(f.Reader, new ListPageAttachments(draftPage.Id)));
    }

    private static Task<PageDto> Create(AttachmentFixture f, string title)
    {
        return f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, title, AttachmentFixture.Rich(title), title)));
    }

    private static Task<AttachmentDto> Upload(AttachmentFixture f, Guid pageId, string text)
    {
        return f.UploadAsync(f.Writer, pageId, "notes.txt", new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }

    private static string Rich(string url)
    {
        return JsonSerializer.Serialize(new
        {
            format = "atlas-rich",
            version = 1,
            plugins = new[] { new { id = "attachments", version = 1 } },
            document = new { type = "doc", content = new[] { new { type = "attachment", attrs = new { data = new { version = 1, name = "file", url } } } } }
        });
    }

    private static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }

    private sealed class CallbackStream : MemoryStream
    {
        private readonly Func<Task> m_beforeEof;
        private bool m_invoked;

        public CallbackStream(byte[] bytes, Func<Task> beforeEof) : base(bytes)
        {
            m_beforeEof = beforeEof;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0 && !m_invoked)
            {
                m_invoked = true;
                await m_beforeEof();
            }
            return read;
        }
    }
}
