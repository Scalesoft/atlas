using System.Text;
using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>ResourceAccessPolicy over ResourceGrantSnapshots and the EffectivePageAccess view must agree for every user, page
/// and operation: a property test over seeded random configurations and a deterministic fixture with expected roles.</summary>
public sealed class ItemAccessEquivalenceTests
{
    private static readonly string[] Validity =
    [
        "NULL,NULL",
        "now()-interval '2 days',now()+interval '2 days'",
        "now()+interval '2 days',NULL",
        "NULL,now()-interval '2 days'",
        "now()-interval '4 days',now()-interval '2 days'",
    ];

    [PostgreSqlFact]
    public async Task Evaluator_and_effective_page_access_view_agree_for_random_configurations()
    {
        foreach (var seed in new[] { 11, 23, 47, 101 })
        {
            await AssertEquivalentAsync(seed);
        }
    }

    [PostgreSqlFact]
    public async Task Evaluator_and_view_agree_on_a_deterministic_fixture_with_expected_roles()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_fixture_");
        var owner = await host.SeedUserAsync("owner");
        var groupAdmin = await host.SeedUserAsync("groupadmin");
        var mixed = await host.SeedUserAsync("mixed");
        var grantOnly = await host.SeedUserAsync("grantonly");
        var groupOnly = await host.SeedUserAsync("grouponly");
        var future = await host.SeedUserAsync("future");
        var expired = await host.SeedUserAsync("expired");
        var former = await host.SeedUserAsync("former");
        var spaceReader = await host.SeedUserAsync("spacereader");
        var inactiveWiki = await host.SeedUserAsync("inactivewiki", isWikiAdmin: true);
        var admins = await host.SeedGroupAsync("Admins", groupAdmin);
        var writers = await host.SeedGroupAsync("Writers", mixed, former);
        var readers = await host.SeedGroupAsync("Readers", groupOnly);
        var space = await host.SeedSpaceAsync("space", (owner, SpaceRole.Writer), (spaceReader, SpaceRole.Reader));
        var shared = await PageAsync(host, space, owner);
        var own = await PageAsync(host, space, owner, ownAccess: true);
        await using var connection = await OpenAsync(host);
        await ExecuteAsync(connection, $"""
            INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role") VALUES (gen_random_uuid(),'{space}','{admins}',3);
            UPDATE "GroupMember" SET "ActiveTo"=clock_timestamp() WHERE "GroupId"='{writers}' AND "UserId"='{former.Id}';
            UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"='{inactiveWiki.Id}';
            """);
        // Space Admin via a group on an own-access page; direct Reader plus group Writer; grant-only and group-only
        // subjects; future and expired grants; a closed membership; an inactive wiki administrator.
        await GrantAsync(connection, own, mixed.Id, null, 1);
        await GrantAsync(connection, own, null, writers, 2);
        await GrantAsync(connection, own, grantOnly.Id, null, 1);
        await GrantAsync(connection, shared, null, readers, 1);
        await GrantAsync(connection, own, future.Id, null, 2, "now()+interval '1 day'");
        await GrantAsync(connection, own, expired.Id, null, 2, "NULL", "now()-interval '1 day'");

        var users = new[] { owner, groupAdmin, mixed, grantOnly, groupOnly, future, expired, former, spaceReader, inactiveWiki };
        var view = await AssertAgreeAsync(host, connection, users, [(shared, space), (own, space)], "fixture");
        var names = users.ToDictionary(u => u.Id, u => u.DisplayName);
        var rows = view.Select(x => $"{names[x.Key.User]},{(x.Key.Page == own ? "own" : "shared")},{x.Value.Role}").Order(StringComparer.Ordinal);
        Assert.Equal(
            ["grantonly,own,1", "groupadmin,own,3", "groupadmin,shared,3", "grouponly,shared,1", "mixed,own,2", "owner,shared,2", "spacereader,shared,1"],
            rows);
    }

    private static async Task AssertEquivalentAsync(int seed)
    {
        var random = new Random(seed);
        await using var host = await AtlasTestHost.CreateAsync("atlas_item_equivalence_");
        var users = new List<UserAccount>();
        for (var index = 0; index < 8; index++)
        {
            users.Add(await host.SeedUserAsync($"user{index}", isWikiAdmin: index is 0 or 1));
        }
        var groups = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var members = users.Skip(1).Where(_ => random.NextDouble() < 0.5).ToList();
            if (index == 0 && !members.Contains(users[3]))
                members.Add(users[3]);
            groups.Add(await host.SeedGroupAsync($"group{index}", members.ToArray()));
        }
        var pages = new List<(Guid Id, Guid SpaceId)>();
        for (var index = 0; index < 2; index++)
        {
            var space = await host.SeedSpaceAsync($"space{index}");
            for (var pageIndex = 0; pageIndex < 5; pageIndex++)
            {
                pages.Add((await PageAsync(host, space, users[2], ownAccess: random.NextDouble() < 0.4), space));
            }
        }
        var subjects = users.Skip(1).Select(u => (Column: "UserId", Id: u.Id)).Concat(groups.Select(g => (Column: "GroupId", Id: g))).ToArray();
        var sql = new StringBuilder();
        foreach (var space in pages.Select(p => p.SpaceId).Distinct())
        {
            foreach (var (column, id) in subjects)
            {
                if (random.NextDouble() < 0.4)
                    sql.AppendLine(Grant("SpaceGrant", "SpaceId", space, column, id, random));
            }
        }
        foreach (var page in pages)
        {
            foreach (var (column, id) in subjects)
            {
                if (random.NextDouble() < 0.25)
                    sql.AppendLine(Grant("ResourceGrant", "PageId", page.Id, column, id, random));
            }
        }
        // users[3] is always seeded into groups[0], so this membership is really closed; a deactivated wiki administrator
        // and member hold no authority either.
        sql.AppendLine($"UPDATE \"GroupMember\" SET \"ActiveTo\"=clock_timestamp() WHERE \"GroupId\"='{groups[0]}' AND \"UserId\"='{users[3].Id}';");
        sql.AppendLine($"UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\" IN ('{users[1].Id}','{users[7].Id}');");
        await using var connection = await OpenAsync(host);
        await ExecuteAsync(connection, sql.ToString());
        Assert.Equal(1L, await ExecuteAsync(connection,
            $"SELECT count(*) FROM \"GroupMember\" WHERE \"GroupId\"='{groups[0]}' AND \"UserId\"='{users[3].Id}' AND \"ActiveTo\" IS NOT NULL"));
        await AssertAgreeAsync(host, connection, users, pages, $"seed {seed}");
    }

    /// <summary>Compares the view and the evaluator for every user, page and operation and returns the view rows.</summary>
    private static async Task<Dictionary<(Guid User, Guid Page), (Guid Space, int Role)>> AssertAgreeAsync(AtlasTestHost host,
        NpgsqlConnection connection, IReadOnlyList<UserAccount> users, IList<(Guid Id, Guid SpaceId)> pages, string label)
    {
        var view = new Dictionary<(Guid User, Guid Page), (Guid Space, int Role)>();
        // The shared database holds other tests' users and pages: only this test's own are read and compared.
        await using (var command = new NpgsqlCommand(
            "SELECT \"UserId\",\"PageId\",\"SpaceId\",\"Role\" FROM \"EffectivePageAccess\" WHERE \"PageId\" = ANY(@pages) AND \"UserId\" = ANY(@users)", connection))
        {
            command.Parameters.AddWithValue("pages", pages.Select(p => p.Id).ToArray());
            command.Parameters.AddWithValue("users", users.Select(u => u.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                view.Add((reader.GetGuid(0), reader.GetGuid(1)), (reader.GetGuid(2), reader.GetInt32(3)));
            }
        }

        using var session = host.OpenSession();
        // One connection for all snapshot reads; test connections are not pooled.
        using var transaction = session.BeginTransaction();
        var snapshots = new ResourceGrantSnapshots(session);
        var allowed = 0;
        var denied = 0;
        foreach (var user in users)
        {
            var actor = (await session.GetAsync<UserAccount>(user.Id))!;
            foreach (var (pageId, spaceId) in pages)
            {
                var page = (await session.GetAsync<Page>(pageId))!;
                var snapshot = await snapshots.ForPageAsync(actor, page, CancellationToken.None);
                var target = new ResourceAccessTarget(page.SpaceId, AccessResourceKind.Document, page.Id, page.OwnAccess);
                var role = view.TryGetValue((user.Id, pageId), out var row) ? row.Role : 0;
                if (role != 0)
                    Assert.Equal(spaceId, row.Space);
                foreach (var operation in Enum.GetValues<AccessOperation>())
                {
                    var decision = ResourceAccessPolicy.Evaluate(snapshot.Principal, target, operation, snapshot.Grants, DateTimeOffset.UtcNow);
                    Assert.True(DocumentRoleAllows(role, operation) == decision.Allowed,
                        $"{label}: {user.Username} page {pages.IndexOf((pageId, spaceId))} own={page.OwnAccess} {operation}: view role {role}, evaluator {decision.Allowed}");
                    if (decision.Allowed) allowed++; else denied++;
                }
            }
        }
        Assert.True(allowed > 0 && denied > 0, $"{label} produced a degenerate configuration");
        return view;
    }

    private static string Grant(string table, string targetColumn, Guid target, string subjectColumn, Guid subject, Random random)
    {
        var role = random.Next(1, 4);
        var validity = Validity[random.Next(Validity.Length)];
        var history = random.NextDouble() < 0.3
            ? $"INSERT INTO \"{table}\"(\"Id\",\"{targetColumn}\",\"{subjectColumn}\",\"Role\",\"ActiveFrom\",\"ActiveTo\") VALUES (gen_random_uuid(),'{target}','{subject}',3,now()-interval '10 days',now()-interval '5 days');"
            : "";
        return history + $"INSERT INTO \"{table}\"(\"Id\",\"{targetColumn}\",\"{subjectColumn}\",\"Role\",\"StartAt\",\"ExpiresAt\") VALUES (gen_random_uuid(),'{target}','{subject}',{role},{validity});";
    }

    /// <summary>The document role matrix, restated independently of the policy under test.</summary>
    private static bool DocumentRoleAllows(int role, AccessOperation operation) => operation switch
    {
        AccessOperation.Discover or AccessOperation.ReadContent => role >= 1,
        AccessOperation.EditContent => role >= 2,
        AccessOperation.ManageAccess or AccessOperation.Share => role >= 3,
        _ => false
    };
}
