using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Requests.Queries;

public sealed class ListAccessRequestsHandler
{
    /// <summary>Candidates read before the manage filter; plenty for an internal instance, and HasMore says when it was not.</summary>
    private const int CandidateLimit = 1000;

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly VaultGrantSnapshots m_grants;

    public ListAccessRequestsHandler(ISession session, IContentAccess content, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_content = content;
        m_grants = grants;
    }

    /// <summary>Only requests whose target the caller manages now; requests of archived vaults and removed secrets drop out with them.</summary>
    public async Task<AccessRequestList> Handle(ListAccessRequests query, CancellationToken ct)
    {
        var state = query.State ?? "pending";
        if (state is not ("pending" or "all"))
        {
            throw new ApiException(400, "Filtr stavu musí být pending nebo all.");
        }
        var actor = await m_content.LockActorAsync(null, ct);
        var where = new List<string> { "v.\"ArchivedAt\" IS NULL", "(s.\"Id\" IS NULL OR s.\"RemovedAt\" IS NULL)" };
        var parameters = new Dictionary<string, object>();
        if (state == "pending")
        {
            where.Add("r.\"State\" = 'pending'");
        }
        if (query.VaultId is { } vaultId)
        {
            where.Add("r.\"VaultId\" = :vault");
            parameters["vault"] = vaultId;
        }
        var candidates = await AccessRequestReads.ListAsync(m_session, m_grants, actor, string.Join(" AND ", where), parameters, CandidateLimit, ct);
        var managed = candidates.Where(r => r.CanManage).ToArray();
        return new AccessRequestList(managed.Take(AccessRequestReads.ListLimit).ToArray(),
            managed.Length > AccessRequestReads.ListLimit || candidates.Count == CandidateLimit);
    }

    public async Task<AccessRequestList> Handle(ListMyAccessRequests query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var mine = await AccessRequestReads.ListAsync(m_session, m_grants, actor, "r.\"RequesterId\" = :actor",
            new Dictionary<string, object> { ["actor"] = actor.Id }, AccessRequestReads.ListLimit + 1, ct);
        return new AccessRequestList(mine.Take(AccessRequestReads.ListLimit).ToArray(), mine.Count > AccessRequestReads.ListLimit);
    }
}
