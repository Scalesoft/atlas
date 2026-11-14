using System.Text;
using System.Text.Json;
using NHibernate;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Platform.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Persistence;

public static class AiProposalState
{
    public const string Pending = "pending";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

/// <summary>The outcome of one item of a decided proposal: <c>applied</c>, <c>failed</c> (with the tool's error code) or <c>skipped</c>;
/// <see cref="PageId"/> the page a new draft became.</summary>
public sealed record AiProposalResult(int Index, string Status, string? Code = null, Guid? PageId = null);

/// <summary>One row of <c>AiToolProposal</c> (migration 2026101630). <see cref="Arguments"/> is the model's JSON object, printed nowhere.</summary>
public sealed record AiToolProposalRow(Guid Id, Guid MessageId, Guid ConversationId, string Tool, string Arguments, IReadOnlyList<AiProposalItemView> Items,
    string State, IReadOnlyList<AiProposalResult>? Results, DateTime CreatedAt, DateTime ExpiresAt, DateTime? DecidedAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>A pending proposal past its expiry reads as expired; the row says so only once somebody acts on it.</summary>
    public string EffectiveState(DateTime now) => State == AiProposalState.Pending && now >= ExpiresAt ? AiProposalState.Expired : State;

    public AiProposalView View(DateTime now)
    {
        var results = (Results ?? []).ToDictionary(result => result.Index);
        return new AiProposalView(Id, MessageId, Tool, EffectiveState(now), ExpiresAt, Items.Select(item => results.TryGetValue(item.Index, out var result)
            ? item with { Status = result.Status, Code = result.Code, PageId = result.PageId ?? item.PageId }
            : item).ToArray());
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Tool = ").Append(Tool).Append(", State = ").Append(State).Append(", Items = ").Append(Items.Count);
        return true;
    }
}

/// <summary>
/// The only SQL of proposals (spec §6.1). Like <see cref="ConversationStore"/>: native SQL, the caller owns the transaction, every
/// read takes the owner, so another person's proposal is simply not found (404). Nothing here logs.
/// </summary>
public sealed class AiProposalStore(ISession session, IAtlasLocks locks)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private const string Columns = """
        p."Id",p."MessageId",p."ConversationId",p."Tool",CAST(p."Arguments" AS text) AS "Arguments",CAST(p."Items" AS text) AS "Items",
        p."State",CAST(p."Results" AS text) AS "Results",p."CreatedAt",p."ExpiresAt",p."DecidedAt"
        """;

    public async Task InsertAsync(AiToolProposalRow row, CancellationToken ct)
        => await session.CreateSQLQuery("""
            INSERT INTO "AiToolProposal"("Id","MessageId","ConversationId","Tool","Arguments","Items","State","CreatedAt","ExpiresAt")
            VALUES (:id,:message,:conversation,:tool,CAST(:arguments AS jsonb),CAST(:items AS jsonb),'pending',:created,:expires)
            """)
            .SetParameter("id", row.Id).SetParameter("message", row.MessageId).SetParameter("conversation", row.ConversationId)
            .SetParameter("tool", row.Tool).SetParameter("arguments", row.Arguments, NHibernateUtil.StringClob)
            .SetParameter("items", JsonSerializer.Serialize(row.Items.Select(item => item with { Status = null, Code = null }), s_json), NHibernateUtil.StringClob)
            .SetParameter("created", row.CreatedAt, NHibernateUtil.UtcDateTime).SetParameter("expires", row.ExpiresAt, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);

    /// <summary>A new message closes the conversation's open proposal (spec §6.1 step 5): superseded, or expired when it already was.</summary>
    public async Task SupersedeAsync(Guid conversationId, DateTime now, CancellationToken ct)
        => await session.CreateSQLQuery("""
            UPDATE "AiToolProposal" SET "State"=CASE WHEN "ExpiresAt"<=:now THEN 'expired' ELSE 'superseded' END,"DecidedAt"=:now
            WHERE "ConversationId"=:conversation AND "State"='pending'
            """)
            .SetParameter("conversation", conversationId).SetParameter("now", now, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);

    /// <summary>The owner's proposal, optionally under its decision lock; null when missing or somebody else's.</summary>
    public async Task<AiToolProposalRow?> OwnedAsync(Guid id, Guid ownerId, bool forUpdate, CancellationToken ct)
    {
        // Decisions on one proposal run one at a time; the state change itself is also a compare-and-set (MoveAsync).
        if (forUpdate) await locks.AcquireAdvisoryAsync($"ai-proposal:{id:N}", ct);
        var rows = await Query("""
            JOIN "AiConversation" c ON c."Id"=p."ConversationId" WHERE p."Id"=:id AND c."OwnerId"=:owner
            """).SetParameter("id", id).SetParameter("owner", ownerId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    /// <summary>The proposals of these answers (the history of an agent turn).</summary>
    public async Task<IReadOnlyList<AiToolProposalRow>> ForMessagesAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
        => messageIds.Count == 0
            ? []
            : (await Query("""WHERE p."MessageId" IN (:ids)""").SetParameterList("ids", messageIds.Distinct().ToArray()).ListAsync<object[]>(ct))
                .Select(ToRow).ToArray();

    /// <summary>Moves the proposal from <paramref name="from"/> to <paramref name="state"/>; false when it is no longer there.</summary>
    public async Task<bool> MoveAsync(Guid id, string from, string state, IReadOnlyList<AiProposalResult>? results, DateTime now, CancellationToken ct)
        => await session.CreateSQLQuery("""
            UPDATE "AiToolProposal" SET "State"=:state,"Results"=CAST(:results AS jsonb),"DecidedAt"=COALESCE("DecidedAt",:now)
            WHERE "Id"=:id AND "State"=:from
            """)
            .SetParameter("id", id).SetParameter("from", from).SetParameter("state", state)
            .SetParameter("results", results is null ? null : JsonSerializer.Serialize(results, s_json), NHibernateUtil.StringClob)
            .SetParameter("now", now, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct) == 1;

    private ISQLQuery Query(string tail) => session.CreateSQLQuery($"""SELECT {Columns} FROM "AiToolProposal" p {tail}""")
        .AddScalar("Id", NHibernateUtil.Guid).AddScalar("MessageId", NHibernateUtil.Guid).AddScalar("ConversationId", NHibernateUtil.Guid)
        .AddScalar("Tool", NHibernateUtil.String).AddScalar("Arguments", NHibernateUtil.StringClob).AddScalar("Items", NHibernateUtil.StringClob)
        .AddScalar("State", NHibernateUtil.String).AddScalar("Results", NHibernateUtil.StringClob).AddScalar("CreatedAt", NHibernateUtil.UtcDateTime)
        .AddScalar("ExpiresAt", NHibernateUtil.UtcDateTime).AddScalar("DecidedAt", NHibernateUtil.UtcDateTime);

    private static AiToolProposalRow ToRow(object[] row) => new((Guid)row[0], (Guid)row[1], (Guid)row[2], (string)row[3], (string)row[4],
        JsonSerializer.Deserialize<AiProposalItemView[]>((string)row[5], s_json) ?? [], (string)row[6],
        row[7] is string results ? JsonSerializer.Deserialize<AiProposalResult[]>(results, s_json) : null,
        (DateTime)row[8], (DateTime)row[9], (DateTime?)row[10]);
}
