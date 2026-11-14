using Npgsql;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
// Alias, not a plain using: inside namespace ...Tests.Search the bare name Search binds to the namespace.
using SearchMessage = Atlas.Server.Features.Search.Queries.Search;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Search;

/// <summary>Search over the real message bus: one disposable schema, four seeded accounts and one space.</summary>
internal sealed class SearchFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private SearchFixture(AtlasTestHost host)
    {
        m_host = host;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host => m_host;
    public AtlasDatabase Database => m_host.Database;
    public TestPages Pages { get; }
    public string ConnectionString => m_host.ConnectionString;
    public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("admin"), DisplayName = "Admin", IsWikiAdmin = true };
    public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer" };
    public UserAccount OtherWriter { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("other"), DisplayName = "Other Writer" };
    public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };
    public Guid SpaceId { get; } = Guid.NewGuid();

    public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

    public Task<PageDto> Create(string title, Guid? parentId = null) => Pages.CreateAsync(new(SpaceId, parentId, title, Rich(title), title), Writer, default);

    // Pooled like production (see DisposableSchema); the draft-corpus benchmark relies on that for comparable timings.
    public static async Task<SearchFixture> CreateAsync(bool pooling = true, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null, bool isolated = false)
    {
        var f = new SearchFixture(isolated
            ? await AtlasTestHost.CreateIsolatedAsync("atlas_search_query_", configure, pooling) /* a test whose subject is a count over the whole table says why (isolated: true at its call) */
            : await AtlasTestHost.CreateAsync("atlas_search_query_", configure, pooling));
        await f.m_host.SeedAsync(async session =>
        {
            foreach (var user in new[] { f.Admin, f.Writer, f.OtherWriter, f.Reader })
            {
                await session.SaveAsync(user);
            }
            await session.SaveAsync(new Space { Id = f.SpaceId, Name = "History" });
            await session.FlushAsync();
            foreach (var user in new[] { f.Writer, f.OtherWriter, f.Reader })
            {
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = user.Id, Role = user == f.Reader ? 1 : 2 });
            }
        });
        return f;
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    // Named Find, not Search: a member named Search would hide the message type at the call site.
    public Task<SearchResponse> Find(UserAccount actor, string? query, SearchOptions? options = null,
        Guid? spaceId = null, int offset = 0, int limit = 20)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<SearchResponse>(new SearchMessage(query, spaceId, offset, limit, options ?? new SearchOptions()));
    }

    // Bare SQL (@id bound to id), then in the same transaction the search documents of only the pages it wrote, refreshed as the code
    // that writes pages does (SearchDocuments.RefreshAsync, the statement RebuildAllSql runs for every page). A rebuild of every page
    // of the shared database deadlocks with the tests writing pages beside it.

    /// <summary>Bare SQL about the page <paramref name="pageId"/>, then that page's search documents.</summary>
    public Task Execute(string sql, Guid pageId) => Execute(sql, pageId, [pageId]);

    /// <summary>Bare SQL whose @id is not a page (a user, a space), then the search documents of <paramref name="pages"/>, the pages it
    /// wrote (none when it writes no page).</summary>
    public Task Execute(string sql, Guid id, IReadOnlyCollection<Guid> pages) => m_host.SeedAsync(async session =>
    {
        await session.CreateSQLQuery(sql.Replace("@id", ":id")).SetParameter("id", id).ExecuteUpdateAsync();
        await SearchDocuments.RefreshAsync(session, pages, default);
    });

    /// <summary>Bare SQL that writes many pages into the fixture's own space, then the search documents of every page of that space
    /// (only this test's pages).</summary>
    public Task ExecuteInSpace(string sql, Guid id) => m_host.SeedAsync(async session =>
    {
        await session.CreateSQLQuery(sql.Replace("@id", ":id")).SetParameter("id", id).ExecuteUpdateAsync();
        var pages = await session.CreateSQLQuery("SELECT \"Id\" AS id FROM \"Page\" WHERE \"SpaceId\"=:space")
            .AddScalar("id", NHibernate.NHibernateUtil.Guid).SetParameter("space", SpaceId).ListAsync<Guid>();
        // In batches of the rebuild job's size: a thousand pages in one statement outlast the command timeout on a busy machine.
        foreach (var batch in pages.Chunk(50))
        {
            await SearchDocuments.RefreshAsync(session, batch, default);
        }
    });

    public async Task<(Guid RevisionId, string Title)> WorkingProjection(Guid pageId)
    {
        using var session = m_host.OpenSession();
        var row = await session.CreateSQLQuery("SELECT \"RevisionId\",\"NormalizedTitle\" FROM \"WorkingSearchDocument\" WHERE \"PageId\"=:id")
            .SetParameter("id", pageId).UniqueResultAsync<object[]>();
        return ((Guid)row[0], (string)row[1]);
    }

    public async Task<string> ExplainDraftSearch(string query, bool latest = false)
    {
        // Optional benchmark diagnostics explain the draft query with its arguments as literals.
        await using var connection = new NpgsqlConnection(m_host.ConnectionString);
        await connection.OpenAsync();
        var body = SearchQueries.Drafts();
        var normalized = SearchText.Normalize(query);
        var expression = SearchText.QueryExpression(query);
        var literals = new Dictionary<string, string>
        {
            [":query"] = Literal(normalized), [":prefix"] = Literal(normalized + "%"), [":fts"] = Literal(expression.Expression),
            [":phrase"] = expression.HasPhrase ? "true" : "false",
            [":fuzzy"] = !expression.HasPhrase && normalized.Length >= 3 ? "true" : "false",
            [":actor"] = Literal(Writer.Id.ToString()) + "::uuid", [":stamp"] = Literal(Writer.SecurityStamp),
            [":space"] = "NULL::uuid", [":changed_since"] = "NULL::timestamptz", [":latest"] = latest ? "true" : "false",
            [":take"] = "21", [":skip"] = "0",
        };
        foreach (var (name, value) in literals.OrderByDescending(pair => pair.Key.Length))
        {
            body = body.Replace(name, value, StringComparison.Ordinal);
        }
        await using var explain = new NpgsqlCommand(
            "SET pg_trgm.word_similarity_threshold=0.45; EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + body, connection);
        return (string)(await explain.ExecuteScalarAsync())!;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
