using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>Moves locked pages to the trash as one batch and back, for the page trash (a page with its subtree) and the space trash
/// (every live page of the space): the one place that writes the trash columns of a page. The reset deletes each page's live log,
/// so no page may hold unsaved edits of others; the actor's own are discarded with the page, like a restore, unless
/// <paramref name="includeOwn"/> (mcp and assistant, D3, S5). Statements stay constant in the number of pages but for the row
/// updates themselves. A page whose trash state changes announces ContentRevised <c>published</c> (<see cref="ContentRevisions"/>).</summary>
public static class PageTrash
{
    /// <param name="where">Where the refused edits are, as the start of the message: „Na stránce“, „Na některé stránce prostoru“.</param>
    public static async Task MoveAsync(ISession session, CollaborationCheckpoints checkpoints, Outbox outbox, IReadOnlyList<Page> pages, Guid actorId,
        Guid batch, DateTime now, string where, bool includeOwn, CancellationToken ct)
    {
        var message = includeOwn
            ? $"{where} jsou neuložené živé úpravy. Počkejte, až se uloží, a zkuste to znovu."
            : $"{where} jsou neuložené živé úpravy jiných uživatelů. Počkejte, až je uloží, a zkuste to znovu.";
        var ids = pages.Select(page => page.Id).ToArray();
        await checkpoints.RequireNoForeignUnsavedAsync(ids, actorId, message, includeOwn, ct);
        var trashed = pages.Where(page => !page.IsTrashed).Select(page => page.Id).ToArray();
        foreach (var item in pages)
        {
            if (!item.IsTrashed)
            {
                outbox.PagePublished(item.Id, item.PublishedRevisionId);
            }
            item.IsTrashed = true;
            item.TrashedAt = now;
            item.TrashedById = actorId;
            item.TrashBatchId = batch;
        }
        // A trashed page ends its share links; restoring it never revives them.
        await ShareRevocation.PagesAsync(session, trashed, actorId, ct);
        await checkpoints.ResetAllAsync(ids, ct);
    }

    public static void Restore(Outbox outbox, IEnumerable<Page> pages)
    {
        foreach (var item in pages)
        {
            if (item.IsTrashed)
            {
                outbox.PagePublished(item.Id, item.PublishedRevisionId);
            }
            item.IsTrashed = false;
            item.TrashedAt = null;
            item.TrashedById = null;
            item.TrashBatchId = null;
        }
    }
}
