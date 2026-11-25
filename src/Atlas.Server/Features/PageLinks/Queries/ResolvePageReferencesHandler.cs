using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.PageLinks.Queries;

/// <summary>Batch verification of rendered page links for the editor: replaces one GET /api/pages/{id} per link, which read
/// every linked body and recorded a page.opened event for each. Ids that are no page the actor may open go, in one query each, to the
/// other content types (a board link, UX PG4-4).</summary>
public sealed class ResolvePageReferencesHandler(IContentAccess access, PageLinkStore store, ContentParticipants participants, NHibernate.ISession session)
{
    public const int MaxIds = 100;

    public async Task<PageReferenceList> Handle(ResolvePageReferences query, CancellationToken ct)
    {
        if (query.Ids is null || query.Ids.Count > MaxIds)
        {
            throw new ApiException(400, "Ověřit lze nejvýše {maxIds} odkazů najednou.", args: new Dictionary<string, object?> { ["maxIds"] = MaxIds });
        }
        // As search: the account under its lock, visibility per page in SQL.
        return await ResolveAsync(await access.LockActorAsync(null, ct), query.Ids, ct);
    }

    /// <summary>The references the account, locked earlier in this transaction, may open (PageBundle).</summary>
    public async Task<PageReferenceList> ResolveAsync(UserAccount actor, IReadOnlyCollection<Guid> requested, CancellationToken ct)
    {
        var ids = requested.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new PageReferenceList([]);
        }
        var found = (await store.ResolveAsync(actor, ids, ct)).ToDictionary(x => x.Id);
        foreach (var participant in participants.References)
        {
            var rest = ids.Where(id => !found.ContainsKey(id)).ToArray();
            if (rest.Length == 0)
            {
                break;
            }
            foreach (var item in await participant.ResolveAsync(actor, rest, ct))
            {
                found[item.Id] = new PageReference(item.Id, item.Title, item.Type);
            }
        }
        // 5.2: the link chip's verification state, one statement for the pages among them.
        var pages = found.Values.Where(x => x.Type is null).Select(x => x.Id).ToList();
        var states = pages.Count == 0 ? [] : await PageVerifications.PageVerificationReader.StatesAsync(session, pages, ct);
        return new PageReferenceList(ids.Where(found.ContainsKey)
            .Select(id => states.TryGetValue(id, out var state) ? found[id] with { Verification = state } : found[id]).ToArray());
    }
}
