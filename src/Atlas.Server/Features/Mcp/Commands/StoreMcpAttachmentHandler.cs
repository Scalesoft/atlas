using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Mcp.Contracts;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>
/// The second transaction of an MCP upload: the shared page guard takes the page row FOR UPDATE in the standard lock order, G5 is
/// decided under those locks (a space switched out of MCP, a page moved, trashed or made uneditable since the preflight refuses the
/// store), and the shared <see cref="StoreAttachmentHandler"/> writes the metadata in this transaction, attributed to the token owner.
/// Until the middleware commits, the blob is provably unreferenced: a refused precondition removes it here, a failing store removes
/// it itself. Nothing removes it on an uncertain COMMIT (the shared orphan policy). The page body, its revisions and its version stay
/// untouched: an upload is not a draft edit and publishes nothing.
/// </summary>
public sealed class StoreMcpAttachmentHandler
{
    private readonly AttachmentPageGuard m_guard;
    private readonly McpOrganizationGuard m_orgGuard;
    private readonly StoreAttachmentHandler m_store;
    private readonly AttachmentFiles m_files;

    public StoreMcpAttachmentHandler(AttachmentPageGuard guard, McpOrganizationGuard orgGuard, StoreAttachmentHandler store, AttachmentFiles files)
    {
        m_guard = guard;
        m_orgGuard = orgGuard;
        m_store = store;
        m_files = files;
    }

    public async Task<McpStoredAttachment> Handle(StoreMcpAttachment command, CancellationToken ct)
    {
        Guid spaceId;
        try
        {
            // Content shared, the space FOR SHARE (the page guard takes it again as a no-op), G5, and only then the page row.
            await m_orgGuard.LockPageSpaceSharedAsync(command.PageId, ct);
            var (page, _, _) = await m_guard.RequirePageAsync(command.PageId, AccessOperation.EditContent, AttachmentPageLock.Update, ct);
            spaceId = page.SpaceId;
        }
        catch (ApiException error) when (error.Status == 404)
        {
            m_files.Remove(command.Id);
            throw new ApiException(404, McpVisibility.MissingPage);
        }
        catch
        {
            m_files.Remove(command.Id);
            throw;
        }
        var stored = await m_store.Handle(new StoreAttachment(command.Id, command.PageId, command.Name, command.Size, command.Sha256,
            command.MimeType, command.IsImage), ct);
        return new McpStoredAttachment(stored, command.PageId, spaceId);
    }
}
