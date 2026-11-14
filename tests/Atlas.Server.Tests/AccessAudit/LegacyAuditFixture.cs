using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Atlas.Server.Platform;
using Atlas.Server.Tests.Platform;
using Wolverine;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>A schema stopped before migration 22, so the upgrade itself can be asserted. AtlasTestHost cannot serve this:
/// it migrates to head on startup. The handler host is built lazily, once the test has run the migration it is testing.</summary>
internal sealed class LegacyAuditFixture : IAsyncDisposable
{
    private readonly IConfiguration m_configuration;
    private IHost? m_host;

    private LegacyAuditFixture(string connectionString)
    {
        ConnectionString = connectionString;
        m_configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:atlas-db"] = connectionString })
            .Build();
    }

    public string ConnectionString { get; }
    public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", DisplayName = "Admin", IsWikiAdmin = true };
    public UserAccount Member { get; } = new() { Id = Guid.NewGuid(), Username = "member", DisplayName = "Member" };
    public Guid SpaceId { get; } = Guid.NewGuid();

    public static async Task<LegacyAuditFixture> CreateAsync()
    {
        // An empty schema in its own database (TestDatabases): the fixture replays the old migrations itself.
        var schema = "atlas_audit_legacy_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(await TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" };
        var fixture = new LegacyAuditFixture(settings.ConnectionString);
        await fixture.ApplyMigrationsBefore22Async();
        return fixture;
    }

    private async Task ApplyMigrationsBefore22Async()
    {
        await Execute("CREATE TABLE \"SchemaVersion\"(\"Version\" bigint PRIMARY KEY,\"AppliedAt\" timestamptz NOT NULL DEFAULT now(),\"Description\" varchar(1024))", null);
        var assembly = typeof(AtlasMigrations).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = int.Parse(resource.Split('.')[^2].Split('_')[0], CultureInfo.InvariantCulture);
            if (version >= 22)
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await Execute(await reader.ReadToEndAsync(), null);
            await Execute($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({version})", null);
        }
        await Execute("INSERT INTO \"UserAccount\" VALUES(@id,'admin','Admin',true,true,'stamp-admin')", Admin.Id);
        Admin.SecurityStamp = "stamp-admin";
        await Execute("INSERT INTO \"UserAccount\" VALUES(@id,'member','Member',false,true,'stamp-member')", Member.Id);
        Member.SecurityStamp = "stamp-member";
        await Execute("INSERT INTO \"Space\" VALUES(@id,'Audit space','book','blue')", SpaceId);
        await using var seed = new NpgsqlConnection(ConnectionString);
        await seed.OpenAsync();
        await using var grant = new NpgsqlCommand(
            "INSERT INTO \"SpaceGrant\"(\"Id\",\"SpaceId\",\"UserId\",\"Role\",\"ActiveFrom\") VALUES(gen_random_uuid(),@space,@user,1,'2020-01-01T00:00:00Z')", seed);
        grant.Parameters.AddWithValue("space", SpaceId);
        grant.Parameters.AddWithValue("user", Member.Id);
        await grant.ExecuteNonQueryAsync();
    }

    public async Task<AccessAuditList> Audit(UserAccount actor)
    {
        var host = await HostAsync();
        host.Services.GetRequiredService<TestActorHolder>().Current = actor;
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<AccessAuditList>(new GetAccessAudit(null, 50));
    }

    /// <summary>Closes the user's current space grants as the application does without an actor (a synchronization): with the audit.</summary>
    public async Task CloseGrantsAsync(Guid userId)
    {
        var host = await HostAsync();
        using var session = host.Services.GetRequiredService<AtlasDatabase>().OpenSession();
        using var transaction = session.BeginTransaction();
        var closed = await session.CreateSQLQuery("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL RETURNING \"Id\"")
            .SetParameter("id", userId).ListAsync<Guid>();
        await AccessAuditLog.ClosedAsync(session, AccessAuditLog.SpaceGrant, [.. closed], null, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<IHost> HostAsync()
    {
        if (m_host is not null)
        {
            return m_host;
        }
        m_host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.ConfigureAtlasWolverine();
                options.Discovery.IncludeAssembly(typeof(LegacyAuditFixture).Assembly);
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(m_configuration);
                services.AddAtlasPlatform();
                services.AddScoped<ICurrentActor, TestCurrentActor>();
                services.AddSingleton<TestActorHolder>();
            })
            .Build();
        await m_host.StartAsync();
        return m_host;
    }

    public async Task Execute(string sql, Guid? id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 90 };
        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (m_host is not null)
        {
            await m_host.StopAsync();
            m_host.Dispose();
        }
        await TestDatabases.DropAsync(ConnectionString);
    }
}
