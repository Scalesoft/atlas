using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Recovery;
using Atlas.Server.Tests.Platform;
using Xunit;
using Atlas.Server.Features.Setup.Commands;
namespace Atlas.Server.Tests;
public class PostgreSqlTests
{
    [PostgreSqlFact]
    public async Task Setup_is_single_winner_and_revisions_are_atomic_UTC_nonoverlapping()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_backend_", services => services.AddAuth());
        var connection = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("atlas-db")!;
        var bootstrap = await host.Services.GetRequiredService<ServerAccessTokens>().EnsureBootstrapAsync();
        var request = new CompleteSetup(bootstrap!, "Test wiki", "admin", "Test Admin", "safe long test passphrase");
        async Task<UserAccount?> Attempt()
        {
            try
            {
                return await host.InvokeAsync<UserAccount>(request);
            }
            catch (ApiException e) when (e.Status == 409)
            {
                return null;
            }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        var admin = Assert.Single(results, x => x is not null)!;
        using var session = host.OpenSession();
        var space = Assert.Single(await session.QueryOver<Space>().ListAsync());
        var pages = new TestPages(host);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var created = await pages.CreateAsync(new(space.Id, null, "First", rich, "first"), admin, default);
        var published = await pages.PublishAsync(created.Id, new(1), admin, default);
        Assert.Equal(1, published.PublishedVersion);
        async Task<PageDto?> Save(string title) { try { return await pages.SaveAsync(created.Id, new(title, rich, title, 1), admin, default); } catch(ApiException e) when(e.Status==409) { return null; } }
        var saves = await Task.WhenAll(Save("Change A"), Save("Change B"));
        var winner = Assert.Single(saves, x => x is not null)!;
        Assert.Equal(2, winner.Version);
        var revisions = (await session.QueryOver<PageRevision>().Where(r => r.PageId==created.Id).OrderBy(r=>r.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(2, revisions.Length);
        Assert.Equal(revisions[0].ActiveTo, revisions[1].ActiveFrom);
        Assert.Null(revisions[1].ActiveTo);
        Assert.All(revisions, r => Assert.Equal(DateTimeKind.Utc, r.ActiveFrom.Kind));
        Assert.Equal("First", revisions[0].Title);
        var savedAgain = await pages.SaveAsync(created.Id, new(winner.Title, rich, winner.Markdown, winner.Version), admin, default);
        Assert.Equal(2, savedAgain.Version);
        await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
        await using var overlap = new NpgsqlCommand("""INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") SELECT gen_random_uuid(),"PageId",99,'invalid','{}','',@author,'2020-01-01'::timestamptz,'2100-01-01'::timestamptz,'web' FROM "PageRevision" WHERE "ActiveTo" IS NULL LIMIT 1""", sql);
        overlap.Parameters.AddWithValue("author", admin.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => overlap.ExecuteNonQueryAsync());
        Assert.Equal("23P01", error.SqlState);
        var reader = new UserAccount { Id = Guid.NewGuid(), Username = "reader", DisplayName = "Reader" };
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(reader);
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space.Id, UserId = reader.Id, Role = (int)SpaceRole.Reader });
            await transaction.CommitAsync();
        }
        var readerPage = await pages.GetAsync(created.Id, reader, default);
        Assert.Equal("First", readerPage.Title);
        Assert.False(readerPage.CanEdit);
        var republished = await pages.PublishAsync(created.Id, new(winner.Version), admin, default);
        Assert.Equal(winner.Version, republished.PublishedVersion);
        Assert.Equal(winner.Title, (await pages.GetAsync(created.Id, reader, default)).Title);
        await pages.PublishAsync(created.Id, new(winner.Version), admin, default);
        var publications = (await session.QueryOver<PagePublication>().Where(p => p.PageId == created.Id).OrderBy(p => p.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(2, publications.Length);
        Assert.Equal(revisions[0].Id, publications[0].RevisionId);
        Assert.Equal(revisions[1].Id, publications[1].RevisionId);
        Assert.Equal(publications[0].ActiveTo, publications[1].ActiveFrom);
        Assert.Null(publications[1].ActiveTo);
        Assert.All(publications, p => Assert.Equal(DateTimeKind.Utc, p.ActiveFrom.Kind));
        Assert.All(publications, p => Assert.Equal(admin.Id, p.PublisherId));
        await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(created.Id, new("unauthorized", rich, "unauthorized", 2), reader, default));
        var draft = await pages.CreateAsync(new(space.Id, null, "Private", rich, "private"), admin, default);
        await Assert.ThrowsAsync<ApiException>(() => pages.GetAsync(draft.Id, reader, default));
    }
}
