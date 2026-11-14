using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Features.SpaceAi.Domain;
using Atlas.Server.Features.SpaceAi.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.SpaceAi;

/// <summary>G5 "Zahrnout prostor do AI" (D11 a): a space admin or a WikiAdmin switches a space in or out of AI. Every space is
/// included until someone excludes it; the switch is temporal with compare-and-swap, and AccessSql.SpaceInAi is the one
/// definition every read over the AI index filters by, so the change takes effect at the next query.</summary>
public sealed class SpaceAiSettingsTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Only_space_admins_and_wiki_admins_change_inclusion_with_cas_and_history()
    {
        await using var f = await Fixture.CreateAsync();

        var initial = await f.Get(f.SpaceAdmin);
        Assert.Equal(new SpaceAiSettingsView(true, null), initial);

        // Who reads the space is told the missing right (403); anyone else learns nothing, the 404 of a missing space; nothing is written.
        var missing = await Assert.ThrowsAsync<ApiException>(() => f.Get(f.SpaceAdmin, Guid.NewGuid()));
        Assert.Equal(404, missing.Status);
        foreach (var (denied, status) in new[] { (f.Writer, 403), (f.Reader, 403), (f.Outsider, 404) })
        {
            foreach (var call in new Func<Task>[] { () => f.Get(denied), () => f.Save(denied, false, null) })
            {
                var exception = await Assert.ThrowsAsync<ApiException>(call);
                Assert.Equal((status, status == 404 ? missing.Message : exception.Message), (exception.Status, exception.Message));
            }
        }
        // Space admin of one space only: the other space, where they merely read, is refused with 403.
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Get(f.SpaceAdmin, f.OtherSpaceId))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Save(f.SpaceAdmin, false, null, f.OtherSpaceId))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Save(f.SpaceAdmin, null, null))).Status);
        Assert.Equal(0, await f.Rows());

        var excluded = await f.Save(f.SpaceAdmin, false, null);
        Assert.False(excluded.Included);
        Assert.NotNull(excluded.Version);
        Assert.Equal(excluded, await f.Get(f.WikiAdmin));
        // A stale version (here: the default's null) is a conflict, not a silent overwrite.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Save(f.WikiAdmin, true, null))).Status);

        var included = await f.Save(f.WikiAdmin, true, excluded.Version);
        Assert.True(included.Included);
        Assert.NotEqual(excluded.Version, included.Version);
        // Saving the current value again keeps the version and writes no new interval.
        Assert.Equal(included, await f.Save(f.SpaceAdmin, true, included.Version));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Save(f.SpaceAdmin, true, excluded.Version))).Status);

        // Temporal: each change is a closed interval with its author, exactly one row is current.
        using var session = f.Host.OpenSession();
        var history = (await session.QueryOver<SpaceAiSetting>().Where(x => x.SpaceId == f.SpaceId).OrderBy(x => x.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal([(false, f.SpaceAdmin.Id), (true, f.WikiAdmin.Id)], history.Select(x => (x.Included, x.ChangedById)));
        Assert.Equal(history[1].ActiveFrom, history[0].ActiveTo);
        Assert.Null(history[1].ActiveTo);
        Assert.Equal(new SpaceAiSettingsView(true, null), await f.Get(f.SpaceAdmin, f.ThirdSpaceId));
    }

    [PostgreSqlFact]
    public async Task Space_in_ai_follows_the_current_row_and_defaults_to_included()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal((true, true), (await f.InAi(f.SpaceId), await f.InAi(f.OtherSpaceId)));

        var excluded = await f.Save(f.SpaceAdmin, false, null);
        // The next statement sees it: no cache and no worker between the switch and retrieval.
        Assert.Equal((false, true), (await f.InAi(f.SpaceId), await f.InAi(f.OtherSpaceId)));

        await f.Save(f.SpaceAdmin, true, excluded.Version);
        // The closed exclusion is history only; the current row decides.
        Assert.Equal((true, true), (await f.InAi(f.SpaceId), await f.InAi(f.OtherSpaceId)));
        Assert.Equal(2, await f.Rows());

        // Retrieval filters rows by it inside one query, like every read over the index will.
        await f.Save(f.WikiAdmin, false, (await f.Get(f.WikiAdmin)).Version);
        using var session = f.Host.OpenSession();
        var spaces = await session.CreateSQLQuery($$"""SELECT "Id" FROM "Space" s WHERE {{AccessSql.SpaceInAi("s.\"Id\"")}} AND "Id" IN (:ids) ORDER BY "Name" """)
            .SetParameterList("ids", new[] { f.SpaceId, f.OtherSpaceId, f.ThirdSpaceId }).ListAsync<Guid>();
        Assert.Equal([f.OtherSpaceId, f.ThirdSpaceId], spaces);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AtlasTestHost host) => Host = host;

        public AtlasTestHost Host { get; }
        public UserAccount WikiAdmin { get; private set; } = null!;
        public UserAccount SpaceAdmin { get; private set; } = null!;
        public UserAccount Writer { get; private set; } = null!;
        public UserAccount Reader { get; private set; } = null!;
        public UserAccount Outsider { get; private set; } = null!;
        public Guid SpaceId { get; private set; }
        public Guid OtherSpaceId { get; private set; }
        public Guid ThirdSpaceId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_space_ai_"));
            f.WikiAdmin = await f.Host.SeedUserAsync("wiki-admin", isWikiAdmin: true);
            f.SpaceAdmin = await f.Host.SeedUserAsync("space-admin");
            f.Writer = await f.Host.SeedUserAsync("writer");
            f.Reader = await f.Host.SeedUserAsync("reader");
            f.Outsider = await f.Host.SeedUserAsync("outsider");
            f.SpaceId = await f.Host.SeedSpaceAsync("A Provoz", (f.SpaceAdmin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
            f.OtherSpaceId = await f.Host.SeedSpaceAsync("B Jiný", (f.SpaceAdmin, SpaceRole.Reader));
            f.ThirdSpaceId = await f.Host.SeedSpaceAsync("C Třetí", (f.SpaceAdmin, SpaceRole.Admin));
            return f;
        }

        public Task<SpaceAiSettingsView> Get(UserAccount actor, Guid? spaceId = null)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<SpaceAiSettingsView>(new GetSpaceAiSettings(spaceId ?? SpaceId));
        }

        public Task<SpaceAiSettingsView> Save(UserAccount actor, bool? included, string? expectedVersion, Guid? spaceId = null)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<SpaceAiSettingsView>(new SaveSpaceAiSettings(spaceId ?? SpaceId, new SaveSpaceAiSettingsRequest(included, expectedVersion)));
        }

        public async Task<bool> InAi(Guid spaceId)
        {
            using var session = Host.OpenSession();
            return await session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":id")}").SetParameter("id", spaceId).UniqueResultAsync<bool>();
        }

        public async Task<int> Rows()
        {
            using var session = Host.OpenSession();
            var spaces = new[] { SpaceId, OtherSpaceId, ThirdSpaceId };
            return await session.QueryOver<SpaceAiSetting>().WhereRestrictionOn(x => x.SpaceId).IsIn(spaces).RowCountAsync();
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
