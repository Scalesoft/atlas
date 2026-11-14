using NHibernate;
using Npgsql;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// Conversations over a disposable schema without any model (plan phase 3 Q1): a WikiAdmin, a writer and a reader of one space,
/// one chat profile revision to pin, and published pages. The store runs in its own session and transaction per call, like
/// one short transaction of the answer flow.
/// </summary>
internal sealed class ConversationFixture : IAsyncDisposable
{
    private ConversationFixture(AtlasTestHost host, UserAccount admin, UserAccount writer, UserAccount reader, Guid spaceId, Guid profileRevisionId)
    {
        Host = host;
        Admin = admin;
        Writer = writer;
        Reader = reader;
        SpaceId = spaceId;
        ChatProfileRevisionId = profileRevisionId;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host { get; }
    public TestPages Pages { get; }
    public UserAccount Admin { get; }
    public UserAccount Writer { get; }
    public UserAccount Reader { get; }
    public Guid SpaceId { get; }
    public Guid ChatProfileRevisionId { get; }
    public string ChatCompatibility { get; } = ChatProfileCompatibility.Fingerprint("openai-compatible", "company", "http://lmstudio.example:1234/v1", "gemma-4");

    public static async Task<ConversationFixture> CreateAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null,
        string? sharedAs = null, bool isolated = false)
    {
        // isolated: a configuration of the fixture's own (no sharedAs) builds a private host, for a test that sweeps whole tables.
        var host = await AtlasTestHost.CreateAsync("atlas_assistant_", isolated ? configure ?? (_ => { }) : configure, sharedAs: sharedAs);
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        var reader = await host.SeedUserAsync("reader");
        var space = await host.SeedSpaceAsync("Provoz", (writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        var profile = Guid.NewGuid();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
            VALUES (:id,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1','gemma-4',120,2,:admin,now())
            """).SetParameter("id", profile).SetParameter("admin", admin.Id).ExecuteUpdateAsync());
        return new ConversationFixture(host, admin, writer, reader, space, profile);
    }

    /// <summary>Now at the database's microsecond precision, so stored times compare equal.</summary>
    public static DateTime Now()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

    /// <summary>A new page published by the writer: its id and published revision.</summary>
    public async Task<(Guid PageId, Guid RevisionId)> PublishAsync(string title, Guid? spaceId = null)
    {
        var page = await Pages.CreateAsync(new CreatePageRequest(spaceId ?? SpaceId, null, title, Rich(title), title), Writer, default);
        await Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), Writer, default);
        return (page.Id, await PublishedRevisionAsync(page.Id));
    }

    /// <summary>A new published revision of the page; returns it.</summary>
    public async Task<Guid> RepublishAsync(Guid pageId, string markdown)
    {
        var current = await Pages.GetAsync(pageId, Writer, default);
        var saved = await Pages.SaveAsync(pageId, new SavePageRequest(current.Title, Rich(markdown), markdown, current.Version), Writer, default);
        await Pages.PublishAsync(pageId, new PublishPageRequest(saved.Version), Writer, default);
        return await PublishedRevisionAsync(pageId);
    }

    public async Task<Guid> PublishedRevisionAsync(Guid pageId)
    {
        using var session = Host.OpenSession();
        return await session.CreateSQLQuery("""SELECT "PublishedRevisionId" FROM "Page" WHERE "Id"=:id""")
            .AddScalar("PublishedRevisionId", NHibernateUtil.Guid).SetParameter("id", pageId).UniqueResultAsync<Guid>();
    }

    /// <summary>One short transaction over the store.</summary>
    public async Task<T> StoreAsync<T>(Func<ConversationStore, Task<T>> action)
    {
        using var session = Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var result = await action(new ConversationStore(session));
        await transaction.CommitAsync();
        return result;
    }

    public Task StoreAsync(Func<ConversationStore, Task> action) => StoreAsync(async store =>
    {
        await action(store);
        return true;
    });

    public async Task<AiConversationRow> StartAsync(UserAccount owner, string origin = AiConversationOrigin.Assistant, DateTime? at = null)
    {
        var now = at ?? Now();
        var conversation = new AiConversationRow(Guid.NewGuid(), owner.Id, origin, origin == AiConversationOrigin.Assistant, ChatProfileRevisionId,
            ChatCompatibility, now, now, 0);
        await StoreAsync(store => store.InsertConversationAsync(conversation, default));
        return conversation;
    }

    /// <summary>A question and its answer (preparing, or no_sources without dependencies) at the next ordinals.</summary>
    public Task<(AiMessageRow Question, AiMessageRow Answer)> TurnAsync(AiConversationRow conversation, string question,
        IReadOnlyList<AiSourceDependencyRow> dependencies, DateTime? at = null, string task = "chat.answer") => StoreAsync(async store =>
    {
        var now = at ?? Now();
        var ordinal = await store.NextOrdinalAsync(conversation.Id, default);
        var user = AiMessageRow.UserQuestion(Guid.NewGuid(), conversation.Id, ordinal, question, AiMessageScope.Wiki, now);
        var answer = AiMessageRow.PreparedAnswer(Guid.NewGuid(), conversation.Id, ordinal + 1,
            dependencies.Count == 0 ? AiMessageState.NoSources : AiMessageState.Preparing, task, AnswerRetrieval.Lexical, "answer@1",
            ChatProfileRevisionId, "gemma-4", now);
        await store.InsertTurnAsync(user, answer, dependencies, default);
        return (user, answer);
    });

    /// <summary>Raw SQL past the store, for the state changes a test makes and for the database's own checks.</summary>
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

    /// <summary>Deletes a page revision, which nothing in Atlas does today; the FK makes any future deletion (or a manual clean-up)
    /// safe. The rows of other features that restrict the deletion go first, found through the catalog so a new feature does not
    /// break the test.</summary>
    public Task DeletePageRevisionAsync(Guid revisionId) => ExecuteAsync($$"""
        DO $do$ DECLARE r record; BEGIN
          FOR r IN SELECT c.conrelid::regclass AS tbl, a.attname FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey, c.confkey) AS k(ref, target)
            JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.ref
            JOIN pg_attribute t ON t.attrelid=c.confrelid AND t.attnum=k.target AND t.attname='Id'
            WHERE c.contype='f' AND c.confrelid='"PageRevision"'::regclass AND c.confdeltype IN ('a','r')
          LOOP
            EXECUTE format('DELETE FROM %s WHERE %I = %L', r.tbl, r.attname, '{{revisionId}}');
          END LOOP;
          DELETE FROM "PageRevision" WHERE "Id"='{{revisionId}}';
        END $do$;
        """);

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
