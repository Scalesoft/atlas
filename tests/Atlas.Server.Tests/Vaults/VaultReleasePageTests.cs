using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.Vaults.Release;
using Xunit;
using static Atlas.Server.Tests.Vaults.SecretReferenceGuardTests;
using static Atlas.Server.Tests.Vaults.VaultReleaseTests;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Releases on a page (Čtenost stránky): the audit keeps the page only once the server confirmed it, and the page's
/// readership lists the releases for the managers of the vault only.</summary>
public sealed class VaultReleasePageTests
{
    [PostgreSqlFact]
    public async Task A_release_keeps_only_a_confirmed_page_and_its_readership_shows_it_to_vault_managers()
    {
        var policy = new FakeReleasePolicy { Decision = new ReleaseDecision("never", "instance", false, true, null) };
        await using var f = await VaultFixture.CreateAsync(release: policy);
        var (vault, router, mail) = await AttachAsync(f);
        var reader = await f.Host.SeedUserAsync("reader");
        var stranger = await f.Host.SeedUserAsync("stranger");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = reader.Id, Role = (int)SpaceRole.Reader }));
        await Grant(f, vault, reader, "read");
        await Grant(f, vault, stranger, "read");
        var pages = f.Host.Pages();
        var created = await pages.CreateAsync(new(f.SpaceId, null, "Síť", Rich(Reference(router)), ""), f.SpaceAdmin, default);
        var page = await pages.PublishAsync(created.Id, new(created.Version), f.SpaceAdmin, default);
        var other = await pages.CreateAsync(new(f.SpaceId, null, "Jiná", Rich(), ""), f.SpaceAdmin, default);
        await pages.PublishAsync(other.Id, new(other.Version), f.SpaceAdmin, default);

        await ReleaseOn(f, reader, router, page.Id, "password", "userName");
        // Not confirmed: a page without the reference, a secret the page does not reference, a page the account cannot read.
        await ReleaseOn(f, reader, router, other.Id, "password");
        await ReleaseOn(f, reader, mail, page.Id, "url");
        await ReleaseOn(f, stranger, router, page.Id, "password");
        await ReleaseOn(f, reader, router, Guid.NewGuid(), "password");
        using (var session = f.Host.OpenSession())
        {
            var kept = await session.CreateSQLQuery("""
                SELECT CAST("Field" || '|' || COALESCE(CAST("PageId" AS text), '-') AS text) FROM "SecretReleaseEvent" WHERE "SecretId" IN (:router, :mail) ORDER BY "Id"
                """).SetParameter("router", router).SetParameter("mail", mail).ListAsync<string>();
            Assert.Equal([$"userName|{page.Id}", $"password|{page.Id}", "password|-", "url|-", "password|-", "password|-"], kept);
        }

        await f.As<PersonalPageState>(reader, new RecordPageVisit(page.Id));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = new GetPageReadership(page.Id, today.AddDays(-1), today.AddDays(1));
        var manager = await f.As<PageReadership>(f.SpaceAdmin, period);
        Assert.Equal((1, 1, 1), (manager.Views, manager.Readers, manager.Secrets));
        var secret = Assert.Single(manager.Items, item => item.Kind == "secret");
        Assert.Equal((reader.Id, "Router", "reveal"), (secret.UserId, secret.SecretTitle, secret.Operation));
        // One entry per release request, the fields in registry order.
        Assert.Equal(["userName", "password"], secret.Fields);
        Assert.Equal(DateTimeKind.Utc, secret.At.Kind);
        Assert.Equal((1, 1), (manager.Days.Sum(d => d.Views), manager.Days.Sum(d => d.Secrets)));

        // Čte on the vault is not enough: a reader of the page sees the views only.
        var plain = await f.As<PageReadership>(reader, period);
        Assert.Equal((1, 0), (plain.Views, plain.Secrets));
        Assert.All(plain.Items, item => Assert.Equal("view", item.Kind));
        Assert.Equal(0, plain.Days.Sum(d => d.Secrets));
    }

    private static async Task ReleaseOn(VaultFixture f, UserAccount actor, Guid secret, Guid page, params string[] fields)
    {
        f.Host.Actor.Current = actor;
        using var scope = f.Host.Services.CreateScope();
        // The audit folds the rows of one request by its trace identifier, as over HTTP.
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { TraceIdentifier = Guid.NewGuid().ToString("N") };
        var (values, _) = await scope.ServiceProvider.GetRequiredService<SecretReleaseService>()
            .ReleaseAsync(secret, new ReleaseSecretRequest(fields, "reveal", null, null, page), CancellationToken.None);
        Assert.NotNull(values);
    }
}
