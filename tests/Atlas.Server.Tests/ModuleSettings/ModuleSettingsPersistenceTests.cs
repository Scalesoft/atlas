using Atlas.Server.Features.ModuleSettings.Domain;
using Xunit;

namespace Atlas.Server.Tests.ModuleSettings;

public sealed class ModuleSettingsPersistenceTests
{
    [PostgreSqlFact]
    public async Task A_new_revision_closes_the_previous_one_on_its_boundary_and_carries_its_own_providers()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync(privateBecause: "closes and replaces the one current sign-in settings revision of the installation (OidcSettingsRevision)");
        var (host, author) = (f.Host, f.Admin);
        var first = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new OidcSettingsRevision { Id = first, PublicOrigin = "https://wiki.example", AuthorId = author.Id, ActiveFrom = DateTime.UtcNow });
            await session.SaveAsync(new OidcProviderSettings { SettingsId = first, ProviderId = "hub", DisplayName = "Hub", Issuer = "https://hub.example", ClientId = "client", AllowSignup = false });
            await session.SaveAsync(new OidcProviderScope { SettingsId = first, ProviderId = "hub", Scope = "openid" });
        });

        var second = Guid.NewGuid();
        DateTime boundary;
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var current = await session.QueryOver<OidcSettingsRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync();
            boundary = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
            current.ActiveTo = boundary;
            // One current revision per installation: the close must reach the database before the replacement insert.
            await session.FlushAsync();
            await session.SaveAsync(new OidcSettingsRevision { Id = second, PublicOrigin = "https://wiki.example", AuthorId = author.Id, ActiveFrom = boundary });
            await session.SaveAsync(new OidcProviderSettings { SettingsId = second, ProviderId = "hub", DisplayName = "Corporate", Issuer = "https://hub.example", ClientId = "client", ProtectedSecret = "protected", AllowSignup = true });
            await session.SaveAsync(new OidcProviderScope { SettingsId = second, ProviderId = "hub", Scope = "openid" });
            await session.SaveAsync(new OidcProviderScope { SettingsId = second, ProviderId = "hub", Scope = "profile" });
            await transaction.CommitAsync();
        }

        using var check = host.OpenSession();
        var currentRevision = await check.QueryOver<OidcSettingsRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(second, currentRevision.Id);
        Assert.Equal(boundary, currentRevision.ActiveFrom);
        var provider = Assert.Single(await check.QueryOver<OidcProviderSettings>().Where(x => x.SettingsId == second).ListAsync());
        Assert.Equal("Corporate", provider.DisplayName);
        Assert.Equal("protected", provider.ProtectedSecret);
        Assert.Equal(2, await check.QueryOver<OidcProviderScope>().Where(x => x.SettingsId == second).RowCountAsync());
        Assert.Equal(1, await check.QueryOver<OidcProviderScope>().Where(x => x.SettingsId == first).RowCountAsync());
    }
}
