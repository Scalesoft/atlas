using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Live;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration;

/// <summary>
/// A save, restore or source replace outside the live session starts a new generation of the page's live document. After the change
/// commits, the read-only peers still joined to an older generation are revalidated (denied: DocumentAccessChanged), so a live page's
/// reader joins the writers' generation at once (3.6-I Živě). A reader never writes, so [SUPERSEDED] never tells it, and the periodic
/// refresh runs only every minute since 2026-11-08. Writers are left alone: their next write meets [SUPERSEDED] as before.
/// </summary>
public sealed class CollaborationGenerationFence(DocumentAccessCoordinator access, ISession session) : IPageChangeObserver
{
    public async Task PageChangedAsync(Guid pageId, string kind, CancellationToken ct)
    {
        var generation = await session.QueryOver<PageCollaboration>().Where(x => x.PageId == pageId)
            .Select(x => x.Generation).SingleOrDefaultAsync<Guid>(ct);
        var prefix = $"page:{pageId:N}:";
        var current = generation == Guid.Empty ? null : new WikiDocumentId(pageId, generation).ToString();
        await access.RefreshReadersAsync(id => id.StartsWith(prefix, StringComparison.Ordinal) && id != current, ct);
    }
}
