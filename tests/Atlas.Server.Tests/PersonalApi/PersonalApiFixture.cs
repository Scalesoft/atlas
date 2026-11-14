using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>A migrated schema with the personal API handlers registered: one space, its writer, a wiki admin and an outsider.</summary>
internal sealed class PersonalApiFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private PersonalApiFixture(AtlasTestHost host)
    {
        m_host = host;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host => m_host;
    public TestPages Pages { get; }
    public Guid SpaceId { get; } = Guid.NewGuid();
    public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("admin"), DisplayName = "Admin", IsWikiAdmin = true };
    public UserAccount Owner { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("owner"), DisplayName = "Owner" };
    public UserAccount Outsider { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("outsider"), DisplayName = "Outsider" };

    /// <summary>The services of the "personal-api" host its tests share.</summary>
    internal static void Services(IServiceCollection services) => services.AddScoped<PersonalApiActor>();

    public static async Task<PersonalApiFixture> CreateAsync()
    {
        var host = await AtlasTestHost.CreateAsync("atlas_personal_api_", Services, sharedAs: "personal-api");
        var fixture = new PersonalApiFixture(host);
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(fixture.Admin);
            await session.SaveAsync(fixture.Owner);
            await session.SaveAsync(fixture.Outsider);
            await session.SaveAsync(new Space { Id = fixture.SpaceId, Name = "Handbook" });
            await session.FlushAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, UserId = fixture.Owner.Id, Role = 2 });
        });
        return fixture;
    }

    public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

    public Task<PageDto> Create(UserAccount actor, string title) => Pages.CreateAsync(new(SpaceId, null, title, Rich(title), title), actor, default);

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    public Task As(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync(message);
    }

    /// <summary>The bearer verification the authentication handler runs, over this schema.</summary>
    public PersonalApiCredentials Credentials => new(m_host.Database);

    /// <summary>The WikiAdmin sets the owner's API level against its current version.</summary>
    public async Task<ApiPermissionDto> SetLevel(string level, UserAccount? user = null)
    {
        var subject = user ?? Owner;
        var current = await As<ApiPermissionDto>(Admin, new GetApiAccess(subject.Id));
        return await As<ApiPermissionDto>(Admin, new SetApiAccess(subject.Id, new ChangeApiPermission(null, current.Version, level)));
    }

    public Task<CreatedApiToken> Issue(string[] scopes, string[]? channels = null, UserAccount? owner = null)
        => As<CreatedApiToken>(owner ?? Owner, new IssueApiToken(new CreateApiToken("Token " + Guid.NewGuid().ToString("N")[..6], scopes, 30, channels)));

    /// <summary>Named parameters on purpose: seeding writes here scope a grant by user and space.</summary>
    public async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var session = m_host.OpenSession();
        using var transaction = session.BeginTransaction();
        var query = session.CreateSQLQuery(sql);
        foreach (var (name, value) in parameters)
        {
            query.SetParameter(name, value);
        }
        await query.ExecuteUpdateAsync();
        await transaction.CommitAsync();
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
