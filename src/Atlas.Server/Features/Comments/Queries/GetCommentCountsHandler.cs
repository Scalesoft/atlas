using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Domain;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Queries;

/// <summary>
/// The quiet badges of many objects at once (a board's cards): one statement counts the live comments of the live objects asked for,
/// then only the objects that have any are authorized, each by its type; an object the reader may not read is left out, as one
/// without comments is.
/// </summary>
public sealed class GetCommentCountsHandler
{
    public const int MaxObjects = 100;

    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly CommentGate m_gate;

    public GetCommentCountsHandler(ISession session, IContentAccess access, CommentGate gate)
    {
        m_session = session;
        m_access = access;
        m_gate = gate;
    }

    public async Task<CommentCounts> Handle(GetCommentCounts query, CancellationToken ct)
    {
        if (query.ObjectIds.Count > MaxObjects)
        {
            throw CommentErrors.Invalid($"Počet komentářů jde zjistit nejvýše u {MaxObjects} objektů najednou.");
        }
        var actor = await m_access.LockActorAsync(null, ct);
        var rows = await m_session.CreateSQLQuery("""
                SELECT c."ObjectId" AS id, r."TypeId" AS type, count(*) AS count
                FROM "Comment" c JOIN "ContentResource" r ON r."Id" = c."ObjectId"
                WHERE c."ObjectId" = ANY(CAST(string_to_array(:ids, ',') AS uuid[])) AND c."DeletedAt" IS NULL AND r."TrashedAt" IS NULL
                GROUP BY c."ObjectId", r."TypeId"
                """)
            .AddScalar("id", NHibernateUtil.Guid).AddScalar("type", NHibernateUtil.String).AddScalar("count", NHibernateUtil.Int32)
            .SetParameter("ids", string.Join(',', query.ObjectIds.Distinct()))
            .ListAsync<object[]>(ct);
        var counts = new Dictionary<Guid, int>();
        foreach (var row in rows)
        {
            if (await m_gate.MayReadAsync(actor, (Guid)row[0], (string)row[1], ct))
            {
                counts[(Guid)row[0]] = (int)row[2];
            }
        }
        return new CommentCounts(counts);
    }
}
