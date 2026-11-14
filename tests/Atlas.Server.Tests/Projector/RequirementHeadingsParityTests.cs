using System.Text.Json.Nodes;
using Npgsql;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The one reading of requirement headings (shape 2026-11-04 §1): <see cref="RequirementTree.Read"/> (RequirementHeadings.cs) answers every
/// case of tests/fixtures/content/requirement-headings.json with its rows, and the index (ProjectorIndex, in code since migration
/// 2026110870) stores exactly them; a case with <c>legacy</c> content (version 3) moves to exactly its content (shape §6,
/// <see cref="Atlas.Server.Platform.Content.RequirementHeadings.MoveParty"/>). The web mirrors the same rules on the same file.
/// </summary>
public sealed class RequirementHeadingsParityTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Fixture())
        {
            data.Add((string)entry["name"]!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_server_reads_the_rows_of_the_fixture(string name)
    {
        var entry = Fixture().Single(x => (string)x["name"]! == name);
        var rows = RequirementTree.Read(Rich(entry), PageId(entry));
        Assert.Equal(Expected(entry), rows.Select(x => Row(x.Position, x.BlockId, x.Key, x.Title, x.Direction, x.State, x.Date, x.Party, x.Where,
            string.Join(",", x.Requires), x.ParentBlockId, x.PartyContactId)).ToArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_move_of_a_version_3_page_gives_the_content_of_the_fixture(string name)
    {
        var entry = Fixture().Single(x => (string)x["name"]! == name);
        if (entry["legacy"] is not JsonArray legacy)
        {
            return;
        }
        var content = (JsonArray)legacy.DeepClone();
        Atlas.Server.Platform.Content.RequirementHeadings.MoveParty(content);
        Assert.Equal(entry["content"]!.ToJsonString(), content.ToJsonString());
        // Moved once: a second move changes nothing.
        Assert.False(Atlas.Server.Platform.Content.RequirementHeadings.MoveParty(content));
    }

    /// <summary>The index in code (migration 2026110870 dropped the SQL function): a page holding each case, refreshed, stores the rows of
    /// <see cref="RequirementTree.Read"/> for that page, the fixture's own rows where the case names its page.</summary>
    [PostgreSqlFact]
    public async Task The_index_stores_the_rows_of_the_fixture()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_projector_headings_");
        string Writer = Guid.NewGuid().ToString(), Space = Guid.NewGuid().ToString();
        var login = TestLogin.For("writer");
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','{login}','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First {login}','book-open','blue');
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }
        // The fixture's page is this test's own page on the shared database: its id stands for the fixture's in the content and the rows.
        var own = Guid.NewGuid().ToString("D");
        foreach (var fixture in Fixture())
        {
            var entry = JsonNode.Parse(System.Text.RegularExpressions.Regex.Replace(fixture.ToJsonString(), FixturePageId, own,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))!;
            var page = PageId(entry) ?? Guid.NewGuid();
            await using (var insert = new NpgsqlCommand($"""
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES (@page,'{Space}','{Writer}',1) ON CONFLICT DO NOTHING;
                UPDATE "PageRevision" SET "ContentJson" = @rich WHERE "PageId" = @page AND "ActiveTo" IS NULL;
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
                  SELECT gen_random_uuid(),@page,1,'Případ',@rich,'','{Writer}',now(),NULL,'web'
                  WHERE NOT EXISTS (SELECT 1 FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL);
                """, connection))
            {
                insert.Parameters.AddWithValue("page", page);
                insert.Parameters.AddWithValue("rich", Rich(entry).ToJsonString());
                await insert.ExecuteNonQueryAsync();
            }
            using (var session = host.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                await ProjectorIndex.RefreshAsync(session, [page], default);
                await transaction.CommitAsync();
            }
            await using var command = new NpgsqlCommand("""
                SELECT "Position","BlockId","Key","Title","Direction","State","Date","Party","Where","Requires","ParentBlockId","PartyContactId"
                FROM "ProjectorRequirementIndex" WHERE "PageId" = @page AND "IsWorking" ORDER BY "Position"
                """, connection);
            command.Parameters.AddWithValue("page", page);
            var rows = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add(Row(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                        reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.GetString(11)));
                }
            }
            var read = RequirementTree.Read(Rich(entry), page).Select(x => Row(x.Position, x.BlockId, x.Key, x.Title, x.Direction, x.State, x.Date, x.Party,
                x.Where, string.Join(",", x.Requires), x.ParentBlockId, x.PartyContactId)).ToArray();
            Assert.True(read.SequenceEqual(rows), $"{entry["name"]}:\n{string.Join("\n", rows)}");
            if (PageId(entry) is not null)
            {
                Assert.True(Expected(entry).SequenceEqual(rows), $"{entry["name"]}:\n{string.Join("\n", rows)}");
            }
        }
    }

    private const string FixturePageId = "0b5f3c1e-0000-4000-8000-0000000000a1";

    private static JsonObject Rich(JsonNode entry) => new()
    {
        ["format"] = "atlas-rich", ["version"] = 1, ["plugins"] = new JsonArray(),
        ["document"] = new JsonObject { ["type"] = "doc", ["content"] = entry["content"]!.DeepClone() },
    };

    private static Guid? PageId(JsonNode entry) => entry["pageId"] is JsonValue id ? Guid.Parse((string)id!) : null;

    private static string[] Expected(JsonNode entry) => entry["rows"]!.AsArray().Select(x => Row((int)x!["position"]!, (string)x["blockId"]!, (string)x["key"]!,
        (string)x["title"]!, (string)x["direction"]!, (string)x["state"]!, (string)x["date"]!, (string)x["party"]!, (string)x["where"]!, (string)x["requires"]!,
        (string?)x["parent"], (string)x["partyContactId"]!)).ToArray();

    private static string Row(int position, string blockId, string key, string title, string direction, string state, string date, string party, string where,
        string requires, string? parent, string contact)
        => string.Join(" | ", position, blockId, key, title, direction, state, date, party, where, requires, parent ?? "∅", contact);

    private static IEnumerable<JsonNode> Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirement-headings.json")))!["cases"]!.AsArray().Select(x => x!);
}
