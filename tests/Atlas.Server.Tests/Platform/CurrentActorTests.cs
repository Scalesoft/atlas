using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class CurrentActorTests
{
    [Fact]
    public async Task Anonymous_request_is_401()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var actor = new HttpCurrentActor(accessor, null!);
        var error = await Assert.ThrowsAsync<ApiException>(() => actor.RequireAsync(CancellationToken.None));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlaste se.", error.Message);
    }

    [PostgreSqlFact]
    public async Task Valid_claims_load_the_account_once_and_stale_stamp_is_401()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_actor_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var user = new UserAccount { Id = Guid.NewGuid(), Username = "u", DisplayName = "U" };
        using (var seed = database.OpenSession())
        using (var transaction = seed.BeginTransaction())
        {
            await seed.SaveAsync(user);
            await transaction.CommitAsync();
        }
        using var session = database.OpenSession();
        var actor = new HttpCurrentActor(Accessor(user.Id, user.SecurityStamp), session);
        var loaded = await actor.RequireAsync(CancellationToken.None);
        Assert.Equal(user.Id, loaded.Id);
        // Evicted from the session, so only the actor's own per-scope cache can return the same instance.
        session.Evict(loaded);
        Assert.Same(loaded, await actor.RequireAsync(CancellationToken.None));

        var stale = new HttpCurrentActor(Accessor(user.Id, "other"), session);
        var error = await Assert.ThrowsAsync<ApiException>(() => stale.RequireAsync(CancellationToken.None));
        Assert.Equal("Přihlášení vypršelo.", error.Message);
    }

    private static IHttpContextAccessor Accessor(Guid id, string stamp)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("stamp", stamp)], "test");
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }
}
