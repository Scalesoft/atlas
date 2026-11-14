using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Npgsql;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Features.SearchGaps.Contracts;
using Atlas.Server.Features.SearchGaps.Queries;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// Feedback and reports over a disposable schema without any model (plan phase 3 Q5): a WikiAdmin, a space admin and three readers
/// of one space, a chat profile revision and a private conversation per reader with answers in the states the tests need. The host is
/// shared: a test tells its feedback rows by its <see cref="ProfileRevisionId"/> and its gap log rows by its space or query.
/// </summary>
internal sealed class FeedbackFixture : IAsyncDisposable
{
    private FeedbackFixture(AtlasTestHost host) => Host = host;

    public AtlasTestHost Host { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount SpaceAdmin { get; private set; } = null!;
    public IReadOnlyList<UserAccount> Readers { get; private set; } = [];
    public Guid SpaceId { get; private set; }
    public Guid PageId { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid ProfileRevisionId { get; } = Guid.NewGuid();

    public static async Task<FeedbackFixture> CreateAsync()
    {
        var f = new FeedbackFixture(await AtlasTestHost.CreateAsync("atlas_feedback_",
            services => services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>(), sharedAs: "feedback"));
        f.Admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        f.SpaceAdmin = await f.Host.SeedUserAsync("space-admin");
        var writer = await f.Host.SeedUserAsync("writer");
        var readers = new List<UserAccount>();
        for (var i = 1; i <= 3; i++)
        {
            readers.Add(await f.Host.SeedUserAsync("reader" + i));
        }
        f.Readers = readers;
        f.SpaceId = await f.Host.SeedSpaceAsync("Provoz", [(f.SpaceAdmin, SpaceRole.Admin), (writer, SpaceRole.Writer),
            .. readers.Select(reader => (reader, SpaceRole.Reader))]);
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
            VALUES (:id,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1','gemma-4',120,2,:admin,now())
            """).SetParameter("id", f.ProfileRevisionId).SetParameter("admin", f.Admin.Id).ExecuteUpdateAsync());
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Tisk", ConversationFixture.Rich("Tisk"), "Tisk"), writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), writer, default);
        f.PageId = page.Id;
        using var session = f.Host.OpenSession();
        f.RevisionId = await session.CreateSQLQuery("""SELECT "PublishedRevisionId" FROM "Page" WHERE "Id"=:id""")
            .AddScalar("PublishedRevisionId", NHibernateUtil.Guid).SetParameter("id", page.Id).UniqueResultAsync<Guid>();
        return f;
    }

    /// <summary>A question and its answer for the owner. <paramref name="state"/> is <c>answered</c> (one cited source),
    /// <c>no_sources</c> or <c>preparing</c>; the scope is the space unless given.</summary>
    public async Task<(Guid QuestionId, Guid AnswerId, Guid ConversationId)> AnswerAsync(UserAccount owner, string question, string state,
        AiMessageScope? scope = null)
    {
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var conversation = new AiConversationRow(Guid.NewGuid(), owner.Id, AiConversationOrigin.Assistant, true, ProfileRevisionId,
            new string('a', 64), now, now, 0);
        var user = AiMessageRow.UserQuestion(Guid.NewGuid(), conversation.Id, 1, question, scope ?? new AiMessageScope(AiScopeKind.Space, SpaceId, null), now);
        var answer = AiMessageRow.PreparedAnswer(Guid.NewGuid(), conversation.Id, 2, state == AiMessageState.NoSources ? AiMessageState.NoSources : AiMessageState.Preparing,
            "chat.answer", AnswerRetrieval.Lexical, "answer@1", ProfileRevisionId, "gemma-4", now);
        await StoreAsync(async store =>
        {
            await store.InsertConversationAsync(conversation, default);
            await store.InsertTurnAsync(user, answer, state == AiMessageState.NoSources ? [] :
                [AiSourceDependencyRow.Page(1, AiDependencyKind.Context, PageId, RevisionId, Guid.NewGuid(), 0, null)], default);
        });
        if (state == AiMessageState.Answered)
        {
            var released = new AiStoredAnswer(true, [new AiStoredSentence("Tiskárna je ve třetím patře.", [1])]);
            await StoreAsync(store => store.CompleteAsync(answer.Id, new AiAnswerCompletion(AiMessageState.Answered, released, null, now.AddSeconds(1), [1]), default));
        }
        return (user.Id, answer.Id, conversation.Id);
    }

    public async Task StoreAsync(Func<ConversationStore, Task> action)
    {
        using var session = Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await action(new ConversationStore(session));
        await transaction.CommitAsync();
    }

    public Task Record(params SearchMiss[] misses) => Host.InvokeAsync(new RecordSearchMisses(misses));

    public Task<SearchGapList> Gaps()
    {
        Host.Actor.Current = SpaceAdmin;
        return Host.InvokeAsync<SearchGapList>(new GetSearchGaps(SpaceId, 30));
    }

    public async Task<List<string>> Strings(string sql)
    {
        using var session = Host.OpenSession();
        return (await session.CreateSQLQuery(sql).ListAsync<string>()).ToList();
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
