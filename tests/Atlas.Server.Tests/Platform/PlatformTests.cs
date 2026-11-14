using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Atlas.Server.Platform;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class PlatformTests
{
    [PostgreSqlFact]
    public async Task OpenSession_before_initialize_is_503_and_works_after_initialize()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_platform_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        Assert.True(database.Configured);
        Assert.False(database.Initialized);
        var error = Assert.Throws<ApiException>(() => database.OpenSession());
        Assert.Equal(503, error.Status);
        await database.InitializeAsync(CancellationToken.None);
        Assert.True(database.Initialized);
        using var session = database.OpenSession();
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT CAST(1 AS bigint)").UniqueResultAsync<long>());
    }

    [Fact]
    public void Sessions_auto_prepare_unless_the_configured_string_says_otherwise()
    {
        var prepared = new NpgsqlConnectionStringBuilder(AtlasDatabase.WithAutoPrepare("Host=db;Database=atlas"));
        Assert.Equal(256, prepared.MaxAutoPrepare);
        Assert.Equal(2, prepared.AutoPrepareMinUsages);
        Assert.Equal("Host=db;Database=atlas;Max Auto Prepare=0", AtlasDatabase.WithAutoPrepare("Host=db;Database=atlas;Max Auto Prepare=0"));
    }

    /// <summary>
    /// A read cancelled while PostgreSQL plans it (a closed page, a dropped hub connection) leaves the session's connection usable for
    /// whoever rents it next, through the raw read and through NHibernate. The sessions auto-prepare, and with Npgsql 10.0.3 the cancelled
    /// run was the one preparing the statement: Npgsql marked it prepared without its row description, and every later run of that
    /// statement on that connection misread the protocol ("Received unexpected backend message DataRow", "No resultset is currently being
    /// traversed") until the failed command forgot its prepared statements (PreparedStatements).
    /// </summary>
    [PostgreSqlFact]
    public async Task A_read_cancelled_while_planned_leaves_the_session_connection_usable()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_platform_cancel_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        using var session = database.OpenSession();
        var none = new Dictionary<string, object>();
        // A statement PostgreSQL takes a while to plan, so a cancellation lands in its planning.
        const string planning = "SELECT set_config('geqo','off',{0}),set_config('join_collapse_limit','20',{0}),set_config('from_collapse_limit','20',{0})";
        await session.ReadRowsAsync(string.Format(planning, "false"), none, row => 0, CancellationToken.None);
        var joins = string.Join(" ", Enumerable.Range(1, 14).Select(i => $"JOIN pg_class c{i} ON c{i}.relpages>=c{i - 1}.relpages-1000000 AND c{i}.reltuples>=c0.reltuples-1e12"));
        var cancelled = 0;
        for (var i = 0; i < 20; i++)
        {
            var sql = $"SELECT c0.relname,{i} FROM pg_class c0 {joins} WHERE c0.relname='pg_class' LIMIT 1";
            Func<CancellationToken, Task<IList<string>>> read = i % 2 == 0
                ? async ct => await session.ReadRowsAsync(sql, none, row => row.GetString(0), ct)
                : async ct =>
                {
                    // As a handler reads: a session and a transaction of its own (a failed one is done), the next one rents the connection.
                    using var handler = database.OpenSession();
                    using var transaction = handler.BeginTransaction();
                    await handler.CreateSQLQuery(string.Format(planning, "true")).ListAsync(CancellationToken.None);
                    return await handler.CreateSQLQuery(sql).AddScalar("relname", NHibernate.NHibernateUtil.String).ListAsync<string>(ct);
                };
            await read(CancellationToken.None);
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1 + i * 3)))
            {
                try
                {
                    await read(cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled++;
                }
            }
            Assert.Equal(["pg_class"], await read(CancellationToken.None));
        }
        Assert.True(cancelled > 0, "no read was cancelled: the statement planned faster than the cancellations");
        // The session's connection does prepare what it repeats (else this test proves nothing).
        var sameTwice = "SELECT count(*) FROM pg_prepared_statements";
        await session.ReadRowsAsync(sameTwice, none, row => row.GetInt64(0), CancellationToken.None);
        Assert.True((await session.ReadRowsAsync(sameTwice, none, row => row.GetInt64(0), CancellationToken.None))[0] > 0);
    }

    [Fact]
    public async Task Unconfigured_database_initialize_is_noop_and_open_is_503()
    {
        using var database = new AtlasDatabase(new ConfigurationBuilder().Build(), NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        Assert.False(database.Initialized);
        Assert.Equal(503, Assert.Throws<ApiException>(() => database.OpenSession()).Status);
    }

    [PostgreSqlFact]
    public async Task AddAtlasPlatform_initializes_on_start_and_gives_each_scope_its_own_session()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_platform_");
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(schema.Configuration);
                services.AddAtlasPlatform();
            })
            .Build();
        await host.StartAsync();
        Assert.True(host.Services.GetRequiredService<AtlasDatabase>().Initialized);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        var b = second.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        Assert.NotSame(a, b);
        Assert.Same(a, first.ServiceProvider.GetRequiredService<NHibernate.ISession>());
        await host.StopAsync();
    }

    [Fact]
    public async Task AddAtlasPlatform_starts_without_connection_string()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                services.AddAtlasPlatform();
            })
            .Build();
        await host.StartAsync();
        Assert.False(host.Services.GetRequiredService<AtlasDatabase>().Initialized);
        await host.StopAsync();
    }

    [PostgreSqlFact]
    public async Task Wolverine_handler_runs_in_its_own_scope_inside_a_transaction_and_rolls_back_on_failure()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_platform_");
        using var callerScope = host.Services.CreateScope();
        var callerSession = callerScope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        var bus = callerScope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
        var handlerSession = (await bus.InvokeAsync<ProbeSessionResult>(new ProbeSession())).Session;
        Assert.NotSame(callerSession, handlerSession);
        Assert.False(handlerSession.IsOpen);
        var secondHandlerSession = (await bus.InvokeAsync<ProbeSessionResult>(new ProbeSession())).Session;
        Assert.NotSame(handlerSession, secondHandlerSession);
        Assert.NotSame(callerSession, secondHandlerSession);
        Assert.True(callerSession.IsOpen);

        var spaceId = Guid.NewGuid();
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(new Space { Id = spaceId, Name = "Original" });
            await transaction.CommitAsync();
        }
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync(new RenameSpaceThenFail(spaceId, "Changed")));
        Assert.Equal(418, error.Status);
        using var check = host.OpenSession();
        Assert.Equal("Original", (await check.GetAsync<Space>(spaceId)).Name);
        Assert.Equal(1L, await check.CreateSQLQuery("SELECT count(*) FROM \"SpaceMetadataRevision\" WHERE \"SpaceId\"=:id").SetParameter("id", spaceId).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Initializer_keeps_retrying_an_unreachable_database_and_stops_once_it_answers()
    {
        // Migrated already: the test is about the retry, and the whole migration chain was its one step that grows with the load of the
        // shared test database, against the fixed waits below (V6).
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_retry_");
        // A real host and port that refuses the connection immediately; every other setting is the working one.
        var unreachable = new NpgsqlConnectionStringBuilder(schema.ConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:atlas-db"] = unreachable })
            .Build();
        var logs = new CaptureLoggerProvider();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                services.AddAtlasPlatform();
                services.AddSingleton(new AtlasDatabaseInitializerOptions { RetryInterval = TimeSpan.FromMilliseconds(100) });
            })
            .Build();
        await host.StartAsync();
        var database = host.Services.GetRequiredService<AtlasDatabase>();
        Assert.True(database.Configured);
        Assert.False(database.Initialized);
        await UntilAsync(() => Retries(logs) >= 3, "the initializer never logged three retries");

        configuration["ConnectionStrings:atlas-db"] = schema.ConnectionString;
        await UntilAsync(() => database.Initialized, "the initializer never picked up the reachable database");
        var afterSuccess = Retries(logs);
        // The loop must exit on its own once the database answers; waiting on the background task proves it.
        var initializer = host.Services.GetServices<IHostedService>().OfType<AtlasDatabaseInitializer>().Single();
        await initializer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(afterSuccess, Retries(logs));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("Database is ready", StringComparison.Ordinal));
        using var session = database.OpenSession();
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT CAST(1 AS bigint)").UniqueResultAsync<long>());
        await host.StopAsync();
    }

    private static int Retries(CaptureLoggerProvider logs)
    {
        return logs.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Database is not ready", StringComparison.Ordinal));
    }

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(condition(), failure);
    }
}
