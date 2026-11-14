using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Domain;

/// <summary>History reads and restores need EditContent on the page: shared locks allow concurrent reads while fencing account, group and grant revocation.</summary>
public sealed class PageHistoryAccess
{
    private readonly ISession m_session;
    private readonly PageGate m_gate;

    public PageHistoryAccess(ISession session, PageGate gate)
    {
        m_session = session;
        m_gate = gate;
    }

    /// <summary>The page through the page gate, FOR SHARE for a read and FOR UPDATE for a restore, decided for EditContent.
    /// Kept from 1.0 (architecture §4.4.1): a reader learns nothing of the history, so every refusal is the 404 of a missing page,
    /// except for who may read the page: it knows the page, so it is told the missing right with 403 (review S3), still nothing more.</summary>
    public async Task<(Page Page, UserAccount Actor)> RequirePageAsync(Guid id, bool mutate, CancellationToken ct)
    {
        if (mutate)
        {
            var edited = Require(await m_gate.EditAsync(id, AccessOperation.EditContent, ct));
            return (edited.Page, edited.Actor);
        }
        var read = Require(await m_gate.ReadAsync(id, AccessOperation.EditContent, ct));
        return (read.Page, read.Actor);
    }

    private static T Require<T>(PageOpening<T> opening) where T : class
    {
        return opening.State == PageOpeningState.Forbidden
            ? throw new ApiException(403, "Historii stránky vidí a obnovuje jen ten, kdo smí stránku upravovat.")
            : opening.RequireHidden();
    }

    public async Task<PageRevision> FindRevisionAsync(Guid id, Guid revisionId, CancellationToken ct)
    {
        return await m_session.QueryOver<PageRevision>()
            .Where(x => x.Id == revisionId && x.PageId == id)
            .SingleOrDefaultAsync(ct)
            ?? throw new ApiException(404, "Verze stránky nebyla nalezena.");
    }

    /// <summary>The last publication of the revision: when it started, what its publisher said it changed and how it came in; null when never published.</summary>
    public async Task<LastPublication?> LastPublishedAsync(Guid id, Guid revisionId, CancellationToken ct)
    {
        var last = await m_session.QueryOver<PagePublication>()
            .Where(x => x.PageId == id && x.RevisionId == revisionId)
            .OrderBy(x => x.ActiveFrom).Desc
            .SelectList(list => list.Select(x => x.ActiveFrom).Select(x => x.ChangeSummary).Select(x => x.Channel).Select(x => x.ApiTokenId))
            .Take(1)
            .ListAsync<object[]>(ct);
        return last.Count == 0 ? null : new LastPublication((DateTime)last[0][0], last[0][1] as string, (string)last[0][2], last[0][3] as Guid?);
    }
}

/// <summary>Metadata of a revision's last publication; never its body.</summary>
public sealed record LastPublication(DateTime At, string? Summary, string Channel, Guid? ApiTokenId);
