using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>A migrated schema with the administration handlers registered: the audit is asserted through the writes that produce it.</summary>
internal sealed class AccessAuditFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private AccessAuditFixture(AtlasTestHost host, UserAccount admin, UserAccount member, Guid spaceId)
    {
        m_host = host;
        Admin = admin;
        Member = member;
        SpaceId = spaceId;
    }

    public AtlasTestHost Host => m_host;
    public AtlasDatabase Database => m_host.Database;
    public UserAccount Admin { get; }
    public UserAccount Member { get; }
    public Guid SpaceId { get; }

    /// <param name="privateBecause">Why the test cannot share the "access-audit" host (DDL, a whole-installation read); it then gets a host of its own.</param>
    public static async Task<AccessAuditFixture> CreateAsync(string? privateBecause = null)
    {
        var host = await AtlasTestHost.CreateAsync("atlas_audit_", sharedAs: privateBecause is null ? "access-audit" : null, configure: services =>
        {
            services.AddAdministration();
            services.AddScoped<PersonalApiActor>();
            // The credential audit function lives in Auth; the handlers under test call it directly.
            services.AddScoped<CredentialAudit>();
            services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
            services.AddSingleton(new BuiltinAuthenticationOptions(true));
            services.AddSingleton(BuiltinPasswordPolicy.Production);
            services.AddSingleton<BuiltinLoginThrottle>();
        });
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var member = await host.SeedUserAsync("member");
        var spaceId = await host.SeedSpaceAsync("Audit space");
        return new AccessAuditFixture(host, admin, member, spaceId);
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    /// <summary>Anonymous messages (activation) pass a null actor.</summary>
    public Task As(UserAccount? actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync(message);
    }

    public Task<AccessAuditList> Audit(UserAccount actor, string? beforeId = null, int limit = 50)
    {
        return As<AccessAuditList>(actor, new GetAccessAudit(beforeId, limit));
    }

    /// <summary>The newest audit event now: the audit lists every test's events, so a test reads those after its mark (<see cref="AuditSinceAsync"/>).</summary>
    public Task<long> MarkAsync() => AuditPin.MarkAsync(m_host.ConnectionString);

    /// <summary>Every audit item after <paramref name="mark"/>, newest first, read page by page as <paramref name="actor"/> reads the audit.</summary>
    public async Task<IReadOnlyList<AccessAuditItem>> AuditSinceAsync(UserAccount actor, long mark)
    {
        var items = new List<AccessAuditItem>();
        for (string? before = null; ;)
        {
            var page = await Audit(actor, before, 100);
            var after = page.Items.Where(x => long.Parse(x.Id, System.Globalization.CultureInfo.InvariantCulture) > mark).ToArray();
            items.AddRange(after);
            if (!page.HasMore || after.Length < page.Items.Count)
            {
                return items;
            }
            before = page.NextBeforeId;
        }
    }

    public async Task<UserAccount> Account(Guid id)
    {
        using var session = m_host.OpenSession();
        return await session.GetAsync<UserAccount>(id);
    }

    /// <summary>Raw ADO.NET on purpose: the immutability assertions need the driver error, not NHibernate's wrapper.</summary>
    public async Task Execute(string sql, Guid? id)
    {
        await using var connection = new NpgsqlConnection(m_host.ConnectionString);
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
        await using var connection = new NpgsqlConnection(m_host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
