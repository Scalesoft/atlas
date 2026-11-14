using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Projector.Commands;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

/// <summary>
/// Undo of one revision's change as a whole (3.6-H): typically an agent's write, which has a revision of its own. Only the blocks that
/// revision changed go back, so what people wrote since elsewhere stays; on a page in live editing it arrives as a collaborator's update
/// (the routes and the tool run it as a live write). People's own undo in their editors is untouched: it never tracks a remote update.
/// </summary>
public sealed record UndoRevisionChange(Guid PageId, Guid RevisionId, int ExpectedVersion);

public sealed class UndoRevisionChangeHandler(ISession session, PageGate gate, McpOrganizationGuard mcp, IRequestChannel channel, SavePageHandler save)
{
    public async Task<PageDto> Handle(UndoRevisionChange command, CancellationToken ct)
    {
        // G5 on MCP, EditContent (a reader 403, anyone else 404), a trashed page is a missing one; then the version.
        var page = (await PageEditing.OpenAsync(gate, mcp, channel, command.PageId, ct)).Page;
        Rules.ExpectVersion(page.Version, command.ExpectedVersion);
        var revision = await session.GetAsync<PageRevision>(command.RevisionId, ct);
        if (revision is null || revision.PageId != page.Id)
        {
            throw new ApiException(404, "Tato verze u stránky není.", "revision_not_found");
        }
        var before = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.Version < revision.Version)
            .OrderBy(x => x.Version).Desc.Take(1).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(409, "První verzi stránky nejde vrátit; stránku můžete přesunout do koše.", "change_not_revertible");
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        var undone = LiveWriteTarget.Undo(current.ContentJson, revision.ContentJson, before.ContentJson)
            ?? throw new ApiException(409, "Místo, které ta změna upravila, se od ní změnilo. Vraťte ji ručně, nebo obnovte celou verzi z historie.",
                "change_not_revertible");
        return await save.Handle(new SavePage(page.Id, new SavePageRequest(current.Title, undone, null, page.Version)), ct);
    }
}
