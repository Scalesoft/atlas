using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Events;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

/// <summary>
/// 3.6, owner 2026-11-05 („server ukládá sám“): the server stores a live page itself, from its live log, when its last editor leaves
/// (<see cref="RequestLiveCheckpoint"/>) and at the latest MarkdownSyncOptions.ServerCheckpointInterval after its first unsaved update
/// (AppendUpdateHandler), so what was typed reaches the revision, search and history without a browser's autosave, a frozen snapshotter
/// tab included. Messages of the outbox, keyed by the page: one at a time per page, retried when a checkpoint fails.
/// </summary>
public sealed record CheckpointLivePage(Guid PageId, Guid Generation) : IOutboxMessage;

/// <summary>The last peer left the live document <paramref name="Generation"/> of the page (PostgresCollaborationStore.LastPeerLeftAsync).</summary>
public sealed record RequestLiveCheckpoint(Guid PageId, Guid Generation);

// IServiceProvider, not IServiceScopeFactory: the generated handler holds Wolverine's own scope factory, and two fields of one type swap names between hosts.
public sealed class LiveCheckpointsHandler(ISession session, Outbox outbox, IServiceProvider services, ILogger<LiveCheckpointsHandler> log)
{
    public async Task Handle(RequestLiveCheckpoint command, CancellationToken ct)
    {
        if ((await UnsavedAuthorsAsync(command.PageId, command.Generation, ct)).Count > 0)
        {
            outbox.Enqueue(new CheckpointLivePage(command.PageId, command.Generation), command.PageId.ToString());
        }
    }

    /// <summary>The checkpoint of what people typed, as the person who typed last on the web (<see cref="LivePageWrites.CheckpointAsync"/>),
    /// under the document gate as every other live write: idempotent, and nothing when it was saved, replaced or left the server meanwhile.
    /// One who may no longer edit the page (deactivated, rights taken) hands it to the next newest author of the tail; with nobody left it
    /// ends quietly: the tail stays durable in the log and the next editor's save stores it.</summary>
    public async Task Handle(CheckpointLivePage command, CancellationToken ct)
    {
        foreach (var author in await UnsavedAuthorsAsync(command.PageId, command.Generation, ct))
        {
            if (await session.GetAsync<UserAccount>(author, ct) is not { IsActive: true } account)
            {
                continue;
            }
            using (ActingAccount.Use(account.Id, account.SecurityStamp, RequestChannel.Web))
            {
                await using var scope = services.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<LivePageWrites>().CheckpointAsync(command.PageId, command.Generation, ct))
                {
                    return;
                }
            }
        }
        log.LogInformation("No author of the unsaved live edits of page {PageId} may edit it now; they wait in the log for the next editor", command.PageId);
    }

    /// <summary>The authors of the unsaved updates, who typed last first, when the generation is current; empty otherwise.</summary>
    private async Task<IReadOnlyList<Guid>> UnsavedAuthorsAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        var head = await session.QueryOver<PageCollaboration>().Where(x => x.PageId == pageId && x.Generation == generation)
            .Select(x => x.Sequence, x => x.LastSavedSequence).SingleOrDefaultAsync<object[]>(ct);
        var saved = (long?)head?[1] ?? 0;
        if (head is null || saved >= (long)head[0])
        {
            return [];
        }
        return await session.Query<CollaborationUpdate>()
            .Where(x => x.PageId == pageId && x.Generation == generation && x.Sequence > saved)
            .GroupBy(x => x.UserId).Select(group => new { Author = group.Key, Last = group.Max(x => x.Sequence) })
            .OrderByDescending(x => x.Last).Select(x => x.Author).ToListAsync(ct);
    }
}
