using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Collaboration.Queries;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PersonalLibrary.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

/// <summary>The page view's page with its <see cref="PageBundle"/> (<c>GET /api/pages/{id}?bundle=true</c>).</summary>
/// <param name="Connections">The connections' query as the endpoint decided it (the page.related capability, from memory).</param>
/// <param name="PersonalState">False when the request records a visit, whose answer is the personal state.</param>
public sealed record GetPageWithBundle(Guid Id, GetPageConnections Connections, bool PersonalState);

/// <summary>Only the bundle's live session, read again once the server seeded it for this open (PagesEndpoints, LiveSeeds).</summary>
public sealed record GetPageBundleCollaboration(Guid Id);
public sealed record PageBundleCollaborationRead(PageBundleCollaboration? Collaboration);

/// <summary>The live Yjs state of the session the bundle names (<c>GET /api/pages/{id}/collaboration/state</c>), checked as the bundle checks it.</summary>
public sealed record GetPageLiveState(Guid Id);
/// <summary><paramref name="State"/>: the stored snapshot and its pending log merged into one Yjs update.</summary>
public sealed record PageLiveState(Guid Generation, byte[] State);
/// <summary>Null when the bundle would name no session, or over the size cap or for a log that does not merge (the editor then joins
/// through the hub, which replays it update by update).</summary>
public sealed record PageLiveStateRead(PageLiveState? Live);

/// <summary>
/// One read transaction: the page as GET /api/pages/{id} opens it, then each part under the page's locks with the check of its own
/// endpoint. Personal state and tags are the page's only where PersonalPageGuard shows them; connections are those of the opened page,
/// filtered per target; contacts and linked pages are filtered per item for the locked account.
/// </summary>
public sealed class GetPageWithBundleHandler(PageReader reader, PersonalPageGuard personal, PageTagStore tags,
    GetPageConnectionsHandler connections, ContactDirectory contacts, ResolvePageReferencesHandler references, ISession session,
    WatchCollaborationHandler watch, PageGate gate, PageVerifications.PageVerificationReader verifications, TimeProvider clock)
{
    private const int MaxStateBytes = 4 * 1024 * 1024;

    public async Task<PageView> Handle(GetPageWithBundle query, CancellationToken ct)
    {
        var (page, opened) = await reader.ReadOpenedAsync(query.Id, ct);
        var actor = opened.Actor;
        var shown = PersonalPageGuard.HasPersonalState(opened);
        var (contactIds, pageIds) = PageBundle.ReferencedIds(page.ContentJson);
        pageIds.Remove(page.Id);
        var verification = await Part(() => verifications.ReadAsync(page.Id, page.PublishedVersion, ct));
        return PageView.From(page) with
        {
            Bundle = new PageBundle(
                shown && query.PersonalState ? await personal.ReadStateAsync(page.Id, actor.Id, ct) : null,
                shown ? await tags.ReadAsync(page.Id, ct) : null,
                await Part(() => connections.ReadAsync(opened, query.Connections, ct)),
                contactIds.Count is 0 or > 200 ? null : await Part(async () => new LiveContactsResult(await contacts.LiveAsync(actor, contactIds, ct))),
                pageIds.Count is 0 or > ResolvePageReferencesHandler.MaxIds ? null : await Part(() => references.ResolveAsync(actor, pageIds, ct)),
                await Part(() => CollaborationAsync(opened, ct)))
            {
                Verification = verification,
                Readers = verification is null || verification.State == PageVerifications.Domain.PageVerificationStates.Off
                    ? null : await PageVerifications.Queries.SpaceReadership.PageReadersAsync(session, page.Id,
                        PageVerifications.PageVerificationReader.ReaderDays, clock.GetUtcNow().UtcDateTime, ct)
            }
        };
    }

    public async Task<PageBundleCollaborationRead> Handle(GetPageBundleCollaboration query, CancellationToken ct)
        => new(await Part(async () => await CollaborationAsync(await OpenAsync(query.Id, ct), ct)));

    public async Task<PageLiveStateRead> Handle(GetPageLiveState query, CancellationToken ct)
    {
        if (await Part(async () => await CollaborationAsync(await OpenAsync(query.Id, ct), ct)) is null)
        {
            return new(null);
        }
        // The row CollaborationAsync has just loaded into the session.
        var state = (await session.GetAsync<PageCollaboration>(query.Id, ct))!;
        var log = await session.QueryOver<CollaborationUpdate>()
            .Where(x => x.PageId == state.PageId && x.Generation == state.Generation && x.Sequence > state.SnapshotSequence)
            .OrderBy(x => x.Sequence).Asc
            .Select(x => x.Payload)
            .ListAsync<byte[]>(ct);
        byte[] merged;
        try
        {
            merged = YjsUpdateMerger.Merge([state.State!, .. log.Select(payload => (ReadOnlyMemory<byte>)payload)], MaxStateBytes);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(null);
        }
        return new(merged.Length > MaxStateBytes ? null : new PageLiveState(state.Generation, merged));
    }

    private async Task<LockedPage> OpenAsync(Guid id, CancellationToken ct) => (await gate.ReadAsync(id, AccessOperation.ReadContent, ct)).Require();

    /// <summary>
    /// The live session exactly when the hub would serve it under its document id: a reader only as the watch answers (a live page), a
    /// writer only when POST …/collaboration would return this generation without creating, resetting or upgrading it. A pure read:
    /// nothing is written. Null without a stored snapshot. Its state is a read of its own (<see cref="GetPageLiveState"/>).
    /// </summary>
    private async Task<PageBundleCollaboration?> CollaborationAsync(LockedPage opened, CancellationToken ct)
    {
        var live = await watch.WatchAsync(opened, ct);
        var state = await session.GetAsync<PageCollaboration>(opened.Page.Id, ct);
        if (state?.State is null || state.StateSha256 is null)
        {
            return null;
        }
        if (opened.CanEdit)
        {
            // Only the id: the page read above already loaded this revision into the session, whose content GetAsync then reuses.
            var currentId = await session.QueryOver<PageRevision>().Where(x => x.PageId == opened.Page.Id && x.ActiveTo == null)
                .Select(x => x.Id).SingleOrDefaultAsync<Guid>(ct);
            if (currentId != state.BaseRevisionId || !CollaborationSchema.OpensAsIs(state, await session.GetAsync<PageRevision>(currentId, ct)))
            {
                return null;
            }
        }
        return new PageBundleCollaboration(live.DocumentId, live.Generation, live.CurrentVersion, !opened.CanEdit);
    }

    /// <summary>shortcut: a part that refuses is null, not an error; upgrade when a part must not fail silently.</summary>
    private static async Task<T?> Part<T>(Func<Task<T>> read) where T : class?
    {
        try
        {
            return await read();
        }
        catch (ApiException)
        {
            return null;
        }
    }
}
