using Atlas.Server.Platform.Events.Commands;
using Atlas.Server.Platform.Events.Queries;
using NHibernate;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Events.Queries
{
    /// <summary>The outbox's state for Správa wiki → Přehled (wiki administrators): Wolverine's envelopes of its queue.</summary>
    public sealed record GetOutboxHealth;

    /// <param name="Pending">Waiting, retrying or sent for another try.</param>
    /// <param name="OldestPendingAt">Always null since 3.3: Wolverine's envelopes keep no time of their writing.</param>
    /// <param name="LastError">The first line of the newest dead letter's error.</param>
    public sealed record OutboxHealth(int Pending, int Dead, DateTime? OldestPendingAt, DateTime? LastFailedAt, string? LastError);

    public sealed class GetOutboxHealthHandler(ISession session, IContentAccess access, SpaceRoles roles)
    {
        public async Task<OutboxHealth> Handle(GetOutboxHealth query, CancellationToken ct)
        {
            await OutboxAdministration.RequireWikiAdminAsync(access, roles, ct);
            var row = await session.CreateSQLQuery("""
                    SELECT (SELECT count(*) FROM wolverine_incoming_envelopes WHERE received_at = :queue AND status <> 'Handled')
                        + count(*) FILTER (WHERE replayable) AS pending,
                      count(*) FILTER (WHERE replayable IS NOT TRUE) AS dead,
                      max(failed_at) FILTER (WHERE replayable IS NOT TRUE) AS failed,
                      (SELECT split_part(exception_message, E'\n', 1) FROM wolverine_dead_letters
                        WHERE received_at = :queue AND replayable IS NOT TRUE ORDER BY failed_at DESC LIMIT 1) AS error
                    FROM wolverine_dead_letters WHERE received_at = :queue
                    """)
                .AddScalar("pending", NHibernateUtil.Int32).AddScalar("dead", NHibernateUtil.Int32)
                .AddScalar("failed", NHibernateUtil.UtcDateTime).AddScalar("error", NHibernateUtil.String)
                .SetParameter("queue", Outbox.QueueUri)
                .UniqueResultAsync<object[]>(ct);
            return new OutboxHealth((int)row[0], (int)row[1], null, (DateTime?)row[2], (string?)row[3]);
        }
    }
}

namespace Atlas.Server.Platform.Events.Commands
{
    /// <summary>Every dead letter of the outbox sent again with fresh attempts: Wolverine's durability agent moves them back to the queue.</summary>
    public sealed record RetryDeadOutboxMessages;

    public sealed record RetriedOutboxMessages(int Retried);

    public sealed class RetryDeadOutboxMessagesHandler(ISession session, IContentAccess access, SpaceRoles roles)
    {
        public async Task<RetriedOutboxMessages> Handle(RetryDeadOutboxMessages command, CancellationToken ct)
        {
            await OutboxAdministration.RequireWikiAdminAsync(access, roles, ct);
            return new RetriedOutboxMessages(await session.CreateSQLQuery("""
                UPDATE wolverine_dead_letters SET replayable = true WHERE received_at = :queue AND replayable IS NOT TRUE
                """).SetParameter("queue", Outbox.QueueUri).ExecuteUpdateAsync(ct));
        }
    }
}

namespace Atlas.Server.Platform.Events
{
    /// <summary>The outbox's dead letters where the wiki administrator sees the wiki's health, with a retry (architecture §5.1).</summary>
    public static class OutboxAdministration
    {
        public static void MapOutboxAdministration(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/admin/outbox").RequireAuthorization();
            group.MapGet("", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<OutboxHealth>(new GetOutboxHealth(), ct)))
                .Produces<OutboxHealth>().ProducesProblem(403);
            group.MapPost("/retry", async (IMessageBus bus, CancellationToken ct)
                    => Results.Ok(await bus.InvokeAsync<RetriedOutboxMessages>(new RetryDeadOutboxMessages(), ct)))
                .Produces<RetriedOutboxMessages>().ProducesProblem(403);
        }

        internal static async Task RequireWikiAdminAsync(IContentAccess access, SpaceRoles roles, CancellationToken ct)
            => await roles.RequireWikiAdminAsync(await access.LockActorAsync(null, ct), ct);
    }
}
