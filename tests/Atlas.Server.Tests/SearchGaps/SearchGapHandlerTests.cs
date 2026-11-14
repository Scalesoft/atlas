using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Features.SearchGaps.Contracts;
using Atlas.Server.Features.SearchGaps.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.SearchGaps;

/// <summary>G2/D9: space admins see what people searched for in their space and did not find, once at least three different
/// people did. No identity is stored, a day is the finest time, and the log keeps 90 days.</summary>
public sealed class SearchGapHandlerTests
{
    private static readonly DateOnly s_today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Misses_count_once_per_user_and_day_and_appear_only_from_three_users()
    {
        await using var f = await Fixture.CreateAsync();

        await f.Record(Miss(f.Writer, f.SpaceId, "Přístup do VPN"), Miss(f.Writer, f.SpaceId, "pristup do vpn"),
            Miss(f.Writer, f.SpaceId, "Přístup do VPN", s_today.AddDays(-1)), Miss(f.Reader, f.SpaceId, "Přístup  do VPN"));
        Assert.Empty((await f.Gaps(f.SpaceAdmin)).Items);

        // The third person reaches the space through one shared page only; the plainer spelling stays the minority.
        await f.Record(Miss(f.Outsider, f.SpaceId, "pristup do VPN"));

        var gaps = await f.Gaps(f.SpaceAdmin);
        var gap = Assert.Single(gaps.Items);
        Assert.Equal(("přístup do vpn", "pristup do vpn", 3, s_today), (gap.Query, gap.NormalizedQuery, gap.Users, gap.LastSearchedOn));
        Assert.Equal((3, 90), (gaps.MinimumUsers, gaps.RetentionDays));
        Assert.Empty((await f.Gaps(f.SpaceAdmin, f.OtherSpaceId)).Items);
    }

    [PostgreSqlFact]
    public async Task Gaps_are_visible_only_to_space_admins_and_wiki_admins()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Record(Miss(f.Writer, f.SpaceId, "dovolená"), Miss(f.Reader, f.SpaceId, "dovolena"), Miss(f.SpaceAdmin, f.SpaceId, "dovolená"));

        Assert.Single((await f.Gaps(f.SpaceAdmin)).Items);
        Assert.Single((await f.Gaps(f.WikiAdmin)).Items);
        foreach (var denied in new[] { f.Writer, f.Reader })
        {
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Gaps(denied))).Status);
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Resolve(denied, "dovolena"))).Status);
        }
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Gaps(f.SpaceAdmin, days: 14))).Status);
    }

    [PostgreSqlFact]
    public async Task Resolved_gap_returns_only_after_three_users_miss_it_again()
    {
        await using var f = await Fixture.CreateAsync();
        var users = new[] { f.Writer, f.Reader, f.SpaceAdmin };
        await f.Record(users.Select(user => Miss(user, f.SpaceId, "tiskárna ve 3. patře")).ToArray());
        var gap = Assert.Single((await f.Gaps(f.SpaceAdmin)).Items);

        await f.Resolve(f.SpaceAdmin, gap.NormalizedQuery);
        await f.Resolve(f.WikiAdmin, gap.NormalizedQuery);
        Assert.Empty((await f.Gaps(f.SpaceAdmin)).Items);
        await f.Record(users.Select(user => Miss(user, f.SpaceId, "tiskárna ve 3. patře")).ToArray());
        Assert.Empty((await f.Gaps(f.SpaceAdmin)).Items);

        var tomorrow = s_today.AddDays(1);
        await f.Record(Miss(f.Writer, f.SpaceId, "tiskarna ve 3 patre", tomorrow), Miss(f.Reader, f.SpaceId, "tiskárna ve 3. patře", tomorrow));
        Assert.Empty((await f.Gaps(f.SpaceAdmin)).Items);
        await f.Record(Miss(f.SpaceAdmin, f.SpaceId, "tiskárna ve 3. patře", tomorrow));
        var again = Assert.Single((await f.Gaps(f.SpaceAdmin)).Items);
        Assert.Equal((3, tomorrow), (again.Users, again.LastSearchedOn));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Resolve(f.SpaceAdmin, " "))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Resolve(f.SpaceAdmin, new string('a', 201)))).Status);
    }

    [PostgreSqlFact]
    public async Task Stored_rows_hold_no_user_identity_and_purge_keeps_ninety_days()
    {
        await using var f = await Fixture.CreateAsync();
        // Through the real flush path: recorder, flusher, RecordSearchMisses in a fresh scope.
        var recorder = f.Host.Services.GetRequiredService<SearchMissRecorder>();
        var now = DateTimeOffset.UtcNow;
        recorder.Observe(new SearchObservation(f.Writer.Id, f.SpaceId, "Návod na VPN", SearchText.Normalize("Návod na VPN"), false), now);
        recorder.Observe(new SearchObservation(f.Writer.Id, f.SpaceId, "dovolená", SearchText.Normalize("dovolená"), false), now);
        var flusher = new SearchMissFlusher(f.Host.Services.GetRequiredService<IServiceScopeFactory>(), recorder, f.Host.Database,
            TimeProvider.System, NullLogger<SearchMissFlusher>.Instance);
        await flusher.FlushAsync(all: true, default);

        var keys = await f.Keys(f.SpaceId);
        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(16, key.Length));
        Assert.NotEqual(Convert.ToHexString(keys[0]), Convert.ToHexString(keys[1]));
        var identity = Convert.ToHexString(f.Writer.Id.ToByteArray());
        Assert.All(keys, key => Assert.All(Enumerable.Range(0, 13), offset =>
            Assert.DoesNotContain(Convert.ToHexString(key, offset, 4), identity, StringComparison.Ordinal)));
        Assert.Equal(["DisplayQuery", "Id", "NormalizedQuery", "OccurredOn", "Reason", "SpaceId", "UserKey"], await f.Columns());

        await f.Record(Miss(f.Reader, f.SpaceId, "stará otázka", s_today.AddDays(-91)), Miss(f.Reader, f.SpaceId, "nová otázka", s_today.AddDays(-89)));
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SearchGapResolution"("SpaceId","NormalizedQuery","ResolvedAt","ResolvedById")
            VALUES (:id,'stara',now()-interval '91 days','{f.WikiAdmin.Id}'),(:id,'nova',now()-interval '89 days','{f.WikiAdmin.Id}')
            """, f.SpaceId);
        // The sweep is whole-table, but the "search-gaps" host holds only this class's rows and no other test writes rows past an age or count.
        await f.Host.Services.GetRequiredService<Atlas.Server.Platform.Retention.OperationalRetention>().RunAsync(default);
        Assert.Equal(["dovolena", "navod na vpn", "nova otazka"], await f.Strings($"""SELECT "NormalizedQuery" FROM "SearchQueryLog" WHERE "SpaceId"='{f.SpaceId}' ORDER BY 1"""));
        Assert.Equal(["nova"], await f.Strings($"""SELECT "NormalizedQuery" FROM "SearchGapResolution" WHERE "SpaceId"='{f.SpaceId}' ORDER BY 1"""));
    }

    [PostgreSqlFact]
    public async Task A_miss_belongs_only_to_a_space_its_user_can_reach()
    {
        await using var f = await Fixture.CreateAsync();
        // The outsider reaches one page of the space and so the space; the writer has nothing in the other space.
        await f.Record(Miss(f.Outsider, f.SpaceId, "kantýna"), Miss(f.Writer, f.OtherSpaceId, "kantýna"), Miss(f.Writer, null, "parkování"));

        Assert.Equal([f.SpaceId.ToString(), "", ""],
            await f.Strings("""SELECT coalesce(CAST("SpaceId" AS text),'') FROM "SearchQueryLog" WHERE "NormalizedQuery" IN ('kantyna','parkovani') ORDER BY "SpaceId" NULLS LAST"""));
    }

    private static SearchMiss Miss(UserAccount user, Guid? space, string query, DateOnly? on = null)
        => new(user.Id, space, SearchText.Normalize(query), SearchMissPolicy.Display(query), on ?? s_today);

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

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_search_gaps_",
                services => services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>(), sharedAs: "search-gaps"));
            f.WikiAdmin = await f.Host.SeedUserAsync("wiki-admin", isWikiAdmin: true);
            f.SpaceAdmin = await f.Host.SeedUserAsync("space-admin");
            f.Writer = await f.Host.SeedUserAsync("writer");
            f.Reader = await f.Host.SeedUserAsync("reader");
            f.Outsider = await f.Host.SeedUserAsync("outsider");
            f.SpaceId = await f.Host.SeedSpaceAsync("Provoz", (f.SpaceAdmin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
            f.OtherSpaceId = await f.Host.SeedSpaceAsync("Jiný", (f.SpaceAdmin, SpaceRole.Admin));
            var pages = new TestPages(f.Host);
            var page = await pages.CreateAsync(new(f.SpaceId, null, "Jídelníček", Search.SearchFixture.Rich("Jídelníček"), "Jídelníček"), f.Writer, default);
            await pages.PublishAsync(page.Id, new(1), f.Writer, default);
            await f.Host.ExecuteAsync($"""
                INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role") VALUES (gen_random_uuid(),:id,'{f.Outsider.Id}',1)
                """, page.Id);
            return f;
        }

        public Task Record(params SearchMiss[] misses) => Host.InvokeAsync(new RecordSearchMisses(misses));

        public Task<SearchGapList> Gaps(UserAccount actor, Guid? spaceId = null, int days = 30)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<SearchGapList>(new GetSearchGaps(spaceId ?? SpaceId, days));
        }

        public Task Resolve(UserAccount actor, string normalizedQuery)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync(new ResolveSearchGap(SpaceId, normalizedQuery));
        }

        public async Task<List<byte[]>> Keys(Guid spaceId)
        {
            using var session = Host.OpenSession();
            return (await session.CreateSQLQuery("""SELECT "UserKey" FROM "SearchQueryLog" WHERE "SpaceId"=:space ORDER BY "Id" """)
                .AddScalar("UserKey", NHibernate.NHibernateUtil.BinaryBlob).SetParameter("space", spaceId).ListAsync<byte[]>()).ToList();
        }

        public Task<List<string>> Columns() => Strings("""
            SELECT CAST(column_name AS text) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='SearchQueryLog' ORDER BY 1
            """);

        public async Task<List<string>> Strings(string sql)
        {
            using var session = Host.OpenSession();
            return (await session.CreateSQLQuery(sql).ListAsync<string>()).ToList();
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
