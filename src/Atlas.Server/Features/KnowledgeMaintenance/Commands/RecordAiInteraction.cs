using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.KnowledgeMaintenance.Commands;

public sealed record RecordAiInteraction(string Task, string Kind, string Outcome)
{
    public override string ToString() => nameof(RecordAiInteraction);
}


/// <summary>Client-reported counters, not an audit trail. The finite catalog prevents arbitrary text reaching storage.</summary>
public sealed class RecordAiInteractionHandler(ISession session, IContentAccess access, IRequestChannel channel, TimeProvider clock)
{
    public async Task<bool> Handle(RecordAiInteraction command, CancellationToken ct)
    {
        await access.LockActorAsync(null, ct);
        if (channel.Current.ApiTokenId is not null)
        {
            throw new ApiException(403, "Hodnocení je dostupné jen v přihlášené wiki.");
        }
        if (!Known(command.Task, command.Kind) || command.Outcome is not ("shown" or "applied" or "dismissed" or "up" or "down"))
        {
            throw new ApiException(400, "Neznámý druh hodnocení.");
        }
        var day = clock.GetUtcNow().UtcDateTime.Date;
        await session.CreateSQLQuery("""
            INSERT INTO "AiInteractionDaily" ("Day","Task","Kind","Outcome","Count") VALUES (:day,:task,:kind,:outcome,1)
            ON CONFLICT ("Day","Task","Kind","Outcome") DO UPDATE SET "Count"="AiInteractionDaily"."Count"+1
            """).SetParameter("day", day, NHibernateUtil.Date).SetParameter("task", command.Task)
            .SetParameter("kind", command.Kind).SetParameter("outcome", command.Outcome).ExecuteUpdateAsync(ct);
        // 90 days are kept: OperationalRetention (AiInteractionDaily) ages the rest out hourly.
        return true;
    }

    private static readonly HashSet<(string Task, string Kind)> s_interactions =
    [
        ("editor.intent", "contact.inline"), ("editor.intent", "contact.card"),
        ("editor.intent", "secret.reference"), ("editor.intent", "callout.wrap"), ("editor.intent", "diagram.fromText"),
        ("editor.link", "link.mention"),
        ("editor.transform", "proofread"), ("editor.transform", "shorten"), ("editor.transform", "simplify"),
        ("editor.transform", "formal"), ("editor.transform", "informal"),
        ("editor.transform", "translate-en"), ("editor.transform", "translate-cs"),
        ("editor.transform", "bullets"), ("editor.transform", "steps"), ("editor.transform", "table"), ("editor.transform", "custom"),
        ("editor.outline", "outline"),
        ("page.create-suggest", "duplicate"), ("page.create-suggest", "location"), ("page.create-suggest", "template"),
        ("publish.change-summary", "change-summary"), ("page.tag-suggest", "tags"),
    ];

    private static bool Known(string task, string kind) => s_interactions.Contains((task, kind));
}
