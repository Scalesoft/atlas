using Atlas.Server.Features.Relations;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Comments;
using Atlas.Server.Features.KnowledgeMaintenance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.AiWriting;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Assistant;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.ExternalSharing;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageHistory;
using Atlas.Server.Features.PageOrganization;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Skills;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Platform;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Modules;
using Atlas.Server.Platform.Events;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Platform;

/// <summary>Real PostgreSQL schema (a clone of the migrated template) + real DI + real Wolverine, no HTTP. The actor is whatever the test sets.</summary>
internal sealed class AtlasTestHost : IAsyncDisposable
{
    private readonly DisposableSchema m_schema;
    private readonly IHost m_host;
    private readonly DirectoryInfo m_contentRoot;
    private bool m_stopped;
    private bool m_shared;

    private AtlasTestHost(DisposableSchema schema, IHost host, DirectoryInfo contentRoot)
    {
        m_schema = schema;
        m_host = host;
        m_contentRoot = contentRoot;
    }

    public IServiceProvider Services => m_host.Services;
    /// <summary>The disposable schema's own connection string, for the few assertions that must bypass NHibernate.</summary>
    public string ConnectionString => m_schema.ConnectionString;
    public AtlasDatabase Database => m_host.Services.GetRequiredService<AtlasDatabase>();
    public TestActorHolder Actor => m_host.Services.GetRequiredService<TestActorHolder>();
    /// <summary>The channel of the next message: web by default (handler tests stand for the web client).</summary>
    public TestChannelHolder Channel => m_host.Services.GetRequiredService<TestChannelHolder>();

    /// <param name="pooling">Npgsql connection pooling, on by default: the pool dies with the schema's database (see DisposableSchema).</param>
    /// <param name="modules">The optional modules this host runs (their services and handlers); none without it.</param>
    /// <param name="durable">Wolverine durable with the jobs queue and scheduler (JobQueue.UseAtlasDurability) and the outbox's queue; without
    /// it requested jobs stay queued and a test runs them by IJobRunner, the outbox sends nothing, and the host opens no durability
    /// connections.</param>
    /// <param name="outbox">Wolverine durable for the outbox's queue, without the scheduler: the outbox delivers, retries soon and dead
    /// letters, no job starts by itself (a requested run still runs), and a test waits for the queue (OutboxTests.DrainAsync).</param>
    /// <param name="sharedAs">Names a configuration that tests share: the first call with the name builds the host from its
    /// arguments and every later call gets that host, whatever it passes. Only for a configuration that is the same for every
    /// caller and holds no state of one test (a fake a test steers is resolved from the host and keeps its state per test).</param>
    public static Task<AtlasTestHost> CreateAsync(string schemaPrefix, Action<IServiceCollection>? configure = null, bool pooling = true,
        ModuleCatalog? modules = null, bool durable = false, bool outbox = false, string? sharedAs = null)
    {
        if (Environment.GetEnvironmentVariable("ATLAS_TEST_PRIVATE_HOSTS") is not null)
        {
            return CreateIsolatedAsync(schemaPrefix, configure, pooling, modules, durable, outbox);
        }
        if (sharedAs is not null)
        {
            return s_named.GetOrAdd(sharedAs, _ => new Lazy<Task<AtlasTestHost>>(async () =>
            {
                var host = await CreateIsolatedAsync(schemaPrefix, configure, pooling, modules, durable, outbox);
                host.m_shared = true;
                return host;
            })).Value;
        }
        return configure is null && pooling && modules is null && !durable && !outbox
            ? s_shared.Value
            : CreateIsolatedAsync(schemaPrefix, configure, pooling, modules, durable, outbox);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<AtlasTestHost>>> s_named = new(StringComparer.Ordinal);

    // The one host of the default configuration and its one database, for every test of the process: tests tell their data apart
    // by their own users and spaces. Never disposed; the database of an ended process is swept by the next (TestDatabases).
    private static readonly Lazy<Task<AtlasTestHost>> s_shared = new(async () =>
    {
        var host = await CreateIsolatedAsync("atlas_shared_");
        host.m_shared = true;
        return host;
    });

    /// <summary>A host and a database of the test's own: for a test that needs an empty or exclusive database, changes state of the
    /// whole host or stops it. Say why at the call.</summary>
    public static async Task<AtlasTestHost> CreateIsolatedAsync(string schemaPrefix, Action<IServiceCollection>? configure = null, bool pooling = true,
        ModuleCatalog? modules = null, bool durable = false, bool outbox = false)
    {
        TestCosts.Private(schemaPrefix);
        return await CreateAsync(await DisposableSchema.CreateMigratedAsync(schemaPrefix, pooling), configure, modules, durable, outbox);
    }

    /// <summary>Hosts an existing schema, e.g. one stopped at an earlier migration; startup migrates it to head. The host owns and drops the schema.</summary>
    public static async Task<AtlasTestHost> CreateAsync(DisposableSchema schema, Action<IServiceCollection>? configure = null, ModuleCatalog? modules = null,
        bool durable = false, bool outbox = false)
    {
        await SharedHandlerCompiler.PrepareAsync();
        var contentRoot = Directory.CreateTempSubdirectory("atlas-test-host-");
        var buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var host = Host.CreateDefaultBuilder()
            .UseContentRoot(contentRoot.FullName)
            .UseWolverine(options =>
            {
                options.ConfigureAtlasWolverine(modules);
                options.Discovery.IncludeAssembly(typeof(AtlasTestHost).Assembly);
                if (durable || outbox)
                {
                    options.UseAtlasDurability(schema.ConnectionString);
                    options.UseAtlasOutbox(retries: [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(100), Outbox.MaxAttempts - 1)]);
                }
                if (outbox && !durable)
                {
                    // A test waits seconds, not minutes, for the outbox's retries, and sees only the jobs it requests itself.
                    options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
                    options.Services.Remove(options.Services.Single(service => service.ImplementationType == typeof(JobScheduler)));
                }
            })
            .ConfigureAppConfiguration((_, configuration) =>
            {
                // Fixtures use only their schema settings. Keep one host-owned configuration and
                // avoid creating unused JSON file watchers for every isolated content root.
                configuration.Sources.Clear();
                configuration.AddConfiguration(schema.Configuration);
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                // AddAttachments()/AddContentExport() need IWebHostEnvironment; the generic Host does not provide
                // one. Fixtures that care about storage paths (AttachmentFixture, ContentExportFixture) override
                // AttachmentFiles/ContentExportFiles with their own instance anyway, so this stub only has to let
                // the type-based registration resolve for hosts that do not.
                // Generic-host services (including server token files) use the same isolated root.
                services.AddSingleton<IWebHostEnvironment>(new AttachmentTests.EnvironmentStub(contentRoot.FullName));
                services.AddAtlasPlatform();
                // Feature modules the handlers under test resolve from; Program.cs registers the same set.
                services.AddPages();
                services.AddContacts();
                Atlas.Server.Features.PersonalItems.PersonalItemsModule.AddPersonalItems(services);
                services.AddComments();
                Atlas.Server.Features.Notifications.NotificationsModule.AddNotifications(services);
                services.AddAttachments();
                services.AddPageHistory();
                services.AddCollaboration();
                services.AddSearch();
                services.AddAssistantSources();
                services.AddKnowledgeMaintenance().AddSearchGaps();
                services.AddPageOrganization();
                Atlas.Server.Features.TreeReferences.TreeReferencesModule.AddTreeReferences(services);
                services.AddPersonalLibrary();
        services.AddApiActivity();
                services.AddPageTags();
                services.AddSkills();
                services.AddPageLinks();
                services.AddPageDecorations();
                Atlas.Server.Features.PageVerifications.PageVerificationsModule.AddPageVerifications(services);
                services.AddRelations();
                services.AddExternalSharing();
                services.AddAi();
                services.AddAiIndex();
                services.AddAssistant().AddAssistantFeedback();
                services.AddAiSuggestions();
                services.AddAiWriting();
                // The token profile and the current user read the MCP module switch; the MCP queries and draft commands need G5,
                // paths, page links and the shared page handlers.
                services.AddMcpModuleState();
                services.AddMcpContent();
                Atlas.Server.Tests.Ai.Fakes.FakeAi.Register(services);
                services.AddScoped<ICurrentActor, TestCurrentActor>();
                services.AddSingleton<TestActorHolder>();
                services.AddScoped<IRequestChannel, TestRequestChannel>();
                services.AddSingleton<TestChannelHolder>();
                SharedHandlerCompiler.Register(services);
                foreach (var module in modules?.Enabled ?? [])
                {
                    module.AddServices(services, schema.Configuration);
                }
                configure?.Invoke(services);
                // Space and page manage grants and the WikiAdmin role are security changes now; handler tests that are not about
                // fresh re-authentication run without the security module, fixtures with it keep the real guard.
                services.TryAddScoped<ISecurityChangeGuard, PermitSecurityChanges>();
            })
            .Build();
        TestCosts.Add("host build", buildStarted);
        await TestCosts.MeasureAsync("host start", () => host.StartAsync());
        return new AtlasTestHost(schema, host, contentRoot);
    }

    public async Task<T> InvokeAsync<T>(object message, CancellationToken ct = default)
    {
        using var scope = m_host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }

    public async Task InvokeAsync(object message, CancellationToken ct = default)
    {
        using var scope = m_host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, ct);
    }

    /// <summary>Runs <paramref name="act"/> under Wolverine's tracking and waits until every message it caused ran, the outbox's
    /// deliveries among them (a durable host); a failure stays the test's to assert.</summary>
    public Task<Wolverine.Tracking.ITrackedSession> TrackAsync(Func<Task> act)
        => Wolverine.Tracking.WolverineHostMessageTrackingExtensions.TrackActivity(m_host).Timeout(TimeSpan.FromSeconds(60))
            .DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync((IMessageContext _) => act());

    public Task GrantAiAsync(UserAccount user)
        => SeedAsync(session => session.SaveAsync(new Atlas.Server.Features.Ai.Access.UserAiPolicy
        {
            Id = Guid.NewGuid(), UserId = user.Id, Enabled = true, ChangedById = user.Id, ActiveFrom = DateTime.UtcNow,
        }));

    public ISession OpenSession() => Database.OpenSession();

    /// <summary>What the outbox's deliveries derive from the pages after their commits (the page links and the contact mentions): a host
    /// without the outbox's queue sends nothing, so a test that reads them calls this once its pages are written.</summary>
    public Task RefreshPageDerivedAsync() => RefreshPageDerivedAsync(Database);

    /// <summary>The same for the pages of the given spaces only, for a shared host: every page of the database would rewrite other
    /// tests' rows and hold an advisory lock per page of theirs.</summary>
    public Task RefreshPageDerivedAsync(params Guid[] spaceIds) => RefreshPageDerivedAsync(Database, spaceIds);

    public static Task RefreshPageDerivedAsync(AtlasDatabase database) => RefreshDerivedAsync(database, null);

    public static Task RefreshPageDerivedAsync(AtlasDatabase database, params Guid[] spaceIds) => RefreshDerivedAsync(database, spaceIds);

    private static async Task RefreshDerivedAsync(AtlasDatabase database, Guid[]? spaceIds)
    {
        using var session = database.OpenSession();
        using var transaction = session.BeginTransaction();
        var links = new PageLinkProjection(session);
        var mentions = new PageObjectMentions(session);
        var pages = session.CreateSQLQuery(spaceIds is null ? """SELECT "Id" FROM "Page" """ : """SELECT "Id" FROM "Page" WHERE "SpaceId" IN (:spaces)""")
            .AddScalar("Id", NHibernate.NHibernateUtil.Guid);
        if (spaceIds is not null)
        {
            pages.SetParameterList("spaces", spaceIds.ToList());
        }
        foreach (var page in await pages.ListAsync<Guid>())
        {
            await links.RefreshAsync(page, default);
            await mentions.RefreshAsync(page, default);
        }
        await transaction.CommitAsync();
    }

    public async Task ExecuteAsync(string sql, Guid id)
    {
        using var session = OpenSession();
        using var transaction = session.BeginTransaction();
        await session.CreateSQLQuery(sql).SetParameter("id", id).ExecuteUpdateAsync();
        await transaction.CommitAsync();
    }

    /// <summary>Runs a seeding delegate in its own session and transaction. Native SQL after entity saves needs FlushAsync first.</summary>
    public async Task SeedAsync(Func<ISession, Task> seed)
    {
        using var session = OpenSession();
        using var transaction = session.BeginTransaction();
        await seed(session);
        await transaction.CommitAsync();
    }

    public async Task<UserAccount> SeedUserAsync(string username, bool isWikiAdmin = false)
    {
        // On the shared host a test's "writer" is not the only one: the name stays the display name, the unique login gets a suffix.
        var login = m_shared ? TestLogin.For(username) : username;
        var user = new UserAccount { Id = Guid.NewGuid(), Username = login, DisplayName = username, IsWikiAdmin = isWikiAdmin };
        // As the application writes an account: with its history and audit (no actor).
        await SeedAsync(async session =>
        {
            await session.SaveAsync(user);
            await AccountHistory.RecordAsync(session, user, null, CancellationToken.None);
        });
        return user;
    }

    public async Task<Guid> SeedSpaceAsync(string name, params (UserAccount User, SpaceRole Role)[] grants)
    {
        var spaceId = Guid.NewGuid();
        await SeedAsync(async session =>
        {
            await session.SaveAsync(new Space { Id = spaceId, Name = name });
            var ids = new List<Guid>();
            foreach (var (user, role) in grants)
            {
                ids.Add(Guid.NewGuid());
                await session.SaveAsync(new SpaceGrant { Id = ids[^1], SpaceId = spaceId, UserId = user.Id, Role = (int)role });
            }
            await AccessAuditLog.OpenedAsync(session, AccessAuditLog.SpaceGrant, ids, null, CancellationToken.None);
        });
        return spaceId;
    }

    /// <summary>Closes current access intervals (the AccessAuditLog target: GroupMemberId, SpaceGrantId or ResourceGrantId rows that
    /// <paramref name="where"/> selects by :id) as the application closes them: with their audit (no actor) and the share links that
    /// lose their issuer's authority (AccessAuditLog.ClosedAsync).</summary>
    public Task CloseAccessAsync(string target, string where, Guid id) => SeedAsync(async session =>
    {
        var closed = await session.CreateSQLQuery(
                $"UPDATE \"{target[..^2]}\" SET \"ActiveTo\"=clock_timestamp() WHERE ({where}) AND \"ActiveTo\" IS NULL RETURNING \"Id\"")
            .SetParameter("id", id).ListAsync<Guid>();
        await AccessAuditLog.ClosedAsync(session, target, [.. closed], null, CancellationToken.None);
    });

    /// <summary>A group and its members as the application writes them: with the group's history and the audit (no actor).</summary>
    public async Task<Guid> SeedGroupAsync(string name, params UserAccount[] members)
    {
        var groupId = Guid.NewGuid();
        await SeedAsync(async session =>
        {
            var group = new Atlas.Server.Features.Administration.Domain.UserGroup { Id = groupId, Name = name };
            await session.SaveAsync(group);
            await Atlas.Server.Features.Administration.Domain.GroupHistory.RecordAsync(session, group, null, CancellationToken.None);
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                ids.Add(Guid.NewGuid());
                await session.CreateSQLQuery("INSERT INTO \"GroupMember\"(\"Id\",\"GroupId\",\"UserId\",\"ActiveFrom\") VALUES (:id,:group,:user,now())")
                    .SetParameter("id", ids[^1])
                    .SetParameter("group", groupId)
                    .SetParameter("user", member.Id)
                    .ExecuteUpdateAsync();
            }
            await AccessAuditLog.OpenedAsync(session, AccessAuditLog.GroupMember, ids, null, CancellationToken.None);
        });
        return groupId;
    }

    /// <summary>Shuts the host down as the server stops, keeping its schema for a next host on it (a restart); DisposeAsync then disposes
    /// the host and drops the schema.</summary>
    public async Task StopAsync()
    {
        if (m_shared)
        {
            throw new InvalidOperationException("The shared host cannot be stopped: create the host with AtlasTestHost.CreateIsolatedAsync.");
        }
        await m_host.StopAsync();
        m_stopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (m_shared)
        {
            return;
        }
        var stopStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!m_stopped)
        {
            await m_host.StopAsync();
        }
        m_host.Dispose();
        TestCosts.Add("host stop", stopStarted);
        await m_schema.DisposeAsync();
        // Created and owned by this host under the OS temp directory; never an application data path.
        m_contentRoot.Delete(recursive: true);
    }
}
