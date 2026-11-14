using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Queries;
using Xunit;
using static Atlas.Server.Tests.Assistant.ConversationSeeds;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Deleting conversations (plan phase 3 §8, spec RAG §7): messages and dependencies go with them, pages and revisions
/// never; data of other features that refers to the messages is changed first (<see cref="IAiConversationDeleting"/>, O20).</summary>
public sealed class ConversationDeleteTests
{
    [PostgreSqlFact]
    public async Task Deleting_one_or_all_removes_messages_and_dependencies_but_never_pages()
    {
        await using var f = await ConversationFixture.CreateAsync(DeletionLog.Register, sharedAs: "conversation-deletions");
        var page = await f.PublishAsync("Zálohování");
        var first = await f.StartAsync(f.Reader);
        var (_, firstAnswer) = await AnsweredAsync(f.Host, first, "Jak zálohuji?", [Source(1, page)], Answer(("Z administrace.", [1])));
        var unlisted = await f.StartAsync(f.Reader, AiConversationOrigin.Search);
        var (_, unlistedAnswer) = await AnsweredAsync(f.Host, unlisted, "Kdy se zálohuje?", [Source(1, page)], Answer(("V noci.", [1])));
        var others = await f.StartAsync(f.Writer);
        await AnsweredAsync(f.Host, others, "Kde jsou zálohy?", [Source(1, page)], Answer(("Na NAS.", [1])));
        // The log of the shared host holds every test's deletions: these are the calls about this test's conversations.
        var log = f.Host.Services.GetRequiredService<DeletionLog>();
        (Guid[] Ids, long Messages)[] Calls() => [.. log.Calls.Where(call => call.Ids.Any(id => id == first.Id || id == unlisted.Id || id == others.Id))];

        await f.Host.As(f.Reader, new DeleteAiConversation(first.Id));

        Assert.Equal((0L, 0L), (await CountAsync(f, "AiMessage", "ConversationId", first.Id), await CountAsync(f, "AiSourceDependency", "MessageId", firstAnswer.Id)));
        // The listener ran before the rows went: the question and the answer were still there.
        var call = Assert.Single(Calls());
        Assert.Equal((first.Id, 2L), (Assert.Single(call.Ids), call.Messages));
        await ConversationReadTests.DeniedAsync(404, () => f.Host.As(f.Reader, new DeleteAiConversation(first.Id)));
        // The unlisted answer from the omnisearch is still there.
        Assert.Equal(1L, await CountAsync(f, "AiSourceDependency", "MessageId", unlistedAnswer.Id));

        // "Smazat všechny konverzace": listed or not, only the owner's.
        await f.Host.As(f.Reader, new DeleteAllAiConversations());
        Assert.Equal(0L, await CountAsync(f, "AiConversation", "OwnerId", f.Reader.Id));
        Assert.Equal([unlisted.Id], Calls()[1].Ids);
        Assert.Single((await f.Host.As<AiConversationList>(f.Writer, new ListAiConversations())).Items);
        // Nothing to delete is not an error, and asks no listener.
        await f.Host.As(f.Reader, new DeleteAllAiConversations());
        Assert.Equal(2, Calls().Length);

        Assert.Equal(page.RevisionId, await f.PublishedRevisionAsync(page.PageId));
        Assert.Equal(1L, await CountAsync(f, "PageRevision", "Id", page.RevisionId));
    }

    internal static async Task<long> CountAsync(ConversationFixture f, string table, string column, Guid id)
    {
        using var session = f.Host.OpenSession();
        return await session.CreateSQLQuery($"""SELECT count(*) AS "Count" FROM "{table}" WHERE "{column}"=:id""")
            .AddScalar("Count", NHibernateUtil.Int64).SetParameter("id", id).UniqueResultAsync<long>();
    }
}

/// <summary>Records each deletion with the number of messages the conversations still had when the listener ran.</summary>
internal sealed class DeletionLog
{
    private readonly ConcurrentQueue<(Guid[] Ids, long Messages)> m_calls = new();

    public IReadOnlyList<(Guid[] Ids, long Messages)> Calls => m_calls.ToArray();

    /// <summary>The host's log: a test reads it back from the host and filters it by its own conversation ids.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<DeletionLog>();
        services.AddScoped<IAiConversationDeleting, Listener>();
    }

    private sealed class Listener(ISession session, DeletionLog log) : IAiConversationDeleting
    {
        public async Task DeletingAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
        {
            var messages = await session.CreateSQLQuery("""SELECT count(*) AS "Count" FROM "AiMessage" WHERE "ConversationId" IN (:ids)""")
                .AddScalar("Count", NHibernateUtil.Int64).SetParameterList("ids", conversationIds.ToArray()).UniqueResultAsync<long>(ct);
            log.m_calls.Enqueue((conversationIds.Order().ToArray(), messages));
        }
    }
}
