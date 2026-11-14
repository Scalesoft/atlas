using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Commands;

/// <summary>A page's working revision opened for block commands: the edit decision with its locks, and the content to change.
/// A class, not a record: the records of this namespace are its commands.</summary>
public sealed class RequirementDraft(EditablePage page, string title, JsonObject rich, int expectedVersion)
{
    public EditablePage Page => page;
    public string Title => title;
    public JsonObject Rich => rich;
    public int ExpectedVersion => expectedVersion;
}

/// <summary>The prologue of every Projector write to a page (review C2-14): on MCP, G5 under the space lock the write keeps; the edit
/// decision (a reader 403, anyone else 404); a trashed page is a missing one.</summary>
internal static class PageEditing
{
    public static async Task<EditablePage> OpenAsync(PageGate gate, McpOrganizationGuard mcp, IRequestChannel channel, Guid pageId, CancellationToken ct)
    {
        if (channel.Current.IsAiBound)
        {
            await mcp.LockPageSpaceSharedAsync(pageId, ct);
        }
        var edited = (await gate.EditAsync(pageId, AccessOperation.EditContent, ct)).Require();
        return edited.Page.IsTrashed ? throw new ApiException(404, "Stránka nebyla nalezena.") : edited;
    }
}

/// <summary>
/// Block changes of a page's working revision, written the way MCP's content tools write it (SaveMcpDraftHandler): the shared
/// <see cref="SavePageHandler"/> in this transaction, so the channel and token, the live-edit guard, password references, the
/// document validation and the new revision are the page save's. The page is opened for editing first, so a reader gets 403, anyone
/// else 404, and a stale version 409 version_conflict before the content is read or changed; on MCP, G5 comes before that, under
/// the space lock the save keeps (a space switched out of MCP is a missing page).
/// </summary>
public sealed class RequirementPageWriter(ISession session, PageGate gate, SavePageHandler save, McpOrganizationGuard mcp, IRequestChannel channel,
    AttachmentFiles files)
{
    public async Task<RequirementDraft> OpenAsync(Guid pageId, int expectedVersion, CancellationToken ct)
    {
        var edited = await PageEditing.OpenAsync(gate, mcp, channel, pageId, ct);
        Rules.ExpectVersion(edited.Page.Version, expectedVersion);
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        return new RequirementDraft(edited, current.Title, JsonNode.Parse(current.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject(),
            expectedVersion);
    }

    /// <summary>Saves the changed content and answers the page's new version. Every block leaves addressable and keyed
    /// (<see cref="RequirementTree.Normalize"/>); attachments the content shows from <paramref name="copyAttachmentsOf"/> (catalog
    /// pages) are copied to the page.</summary>
    public async Task<int> SaveAsync(RequirementDraft draft, CancellationToken ct, IReadOnlyCollection<Guid>? copyAttachmentsOf = null)
    {
        RequirementTree.Normalize(draft.Rich);
        var pageId = draft.Page.Page.Id;
        var copy = await AttachmentCopies.CopyAsync(session, files, draft.Rich.ToJsonString(RichDocument.JsonWrite), copyAttachmentsOf ?? [], pageId,
            draft.Page.Actor.Id, ct);
        return await AttachmentCopies.DiscardOnFailureAsync(files, copy.Files,
            async () => (await save.Handle(new SavePage(pageId, new SavePageRequest(draft.Title, copy.ContentJson, null, draft.ExpectedVersion)), ct)).Version);
    }
}
