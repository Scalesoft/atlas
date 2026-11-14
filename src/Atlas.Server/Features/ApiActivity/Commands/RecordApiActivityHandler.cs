using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ApiActivity.Commands;

public sealed class RecordApiActivityHandler
{
    private readonly ISession m_session;

    public RecordApiActivityHandler(ISession session)
    {
        m_session = session;
    }

    public async Task Handle(RecordApiActivity command, CancellationToken ct)
    {
        // The access audit event in the same statement: api.write; assistant.* or mcp.*, publish for atlas_publish. The space of a page
        // target is the one it is in now, when the caller does not know it (a refusal before the handler read the page).
        var action = command.Channel switch
        {
            "api" => "api.write",
            "assistant" => command.Operation == "atlas_publish" ? "assistant.publish" : "assistant.write",
            _ => command.Operation == "atlas_publish" ? "mcp.publish" : "mcp.write",
        };
        await m_session.CreateSQLQuery("""
            WITH activity AS (
                INSERT INTO "ApiActivity"("ActorId","TokenId","Channel","Operation","PageId","SpaceId","Outcome","ResultVersion","RequestId")
                VALUES (:actor,:token,:channel,:operation,:page,COALESCE(:space,(SELECT "SpaceId" FROM "Page" WHERE "Id"=:page)),:outcome,:version,:request)
                RETURNING "Id")
            INSERT INTO "AccessAuditEvent"("ActorId","Action","ApiActivityId") SELECT :actor, :action, "Id" FROM activity
            """)
            .SetParameter("action", action)
            .SetParameter("actor", command.ActorId).SetParameter("token", command.TokenId, NHibernateUtil.Guid)
            .SetParameter("channel", command.Channel).SetParameter("operation", command.Operation)
            .SetParameter("page", command.PageId, NHibernateUtil.Guid).SetParameter("space", command.SpaceId, NHibernateUtil.Guid)
            .SetParameter("outcome", command.Outcome).SetParameter("version", command.ResultVersion, NHibernateUtil.Int32)
            .SetParameter("request", command.RequestId, NHibernateUtil.String)
            .ExecuteUpdateAsync(ct);
    }
}
