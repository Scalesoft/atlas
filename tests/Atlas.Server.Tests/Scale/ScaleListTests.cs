using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Scale;

/// <summary>Runs only with ATLAS_SCALE=1 (and ATLAS_TEST_CONNECTION): seeding 10 000 pages, accounts and contacts takes a while.</summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTION")) || Environment.GetEnvironmentVariable("ATLAS_SCALE") != "1")
        {
            Skip = "Set ATLAS_SCALE=1 and ATLAS_TEST_CONNECTION to measure the list endpoints at 10 000 pages and accounts.";
        }
    }
}

/// <summary>
/// A measurement, not a budget: the list endpoints over one space of 10 000 published pages (50 top-level, depth 4), 10 000 accounts in
/// three groups (one holds everyone and reads the space), 10 000 wiki contacts (200 same-name pairs), 300 comments on one page and
/// 300 notifications of one member. Rows go in set-based through the real tables; the schema's triggers derive the rest (search
/// documents, organization history, content resources), and the index tasks they queue are dropped; bare SQL announces nothing to the
/// outbox, so no background work runs during the timings. Each read is timed cold and warm and written to artifacts/scale/scale-10k.md;
/// only a 2xx answer is asserted.
/// </summary>
public sealed class ScaleListTests(ITestOutputHelper output)
{
    private const int Count = 10_000;

    [ScaleFact]
    [Trait("Category", "Scale")]
    public async Task List_endpoints_at_ten_thousand_pages_and_accounts()
    {
        // The linking sweep is timed below on its own: the site leaves its worker out (and the accounts' contacts' worker, whose R3
        // contacts would change the place's names every pass, which starts the extraction over each time).
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_scale_") /* own database: seeds and measures a whole installation (counts every account, clears the index queue, ANALYZE, sweeps every page) */;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var adminId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/auth/me"), HttpStatusCode.OK))["id"]!.GetValue<Guid>();
        var space = (await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<Guid>();
        var everyone = await GroupAsync(admin, "Všichni");
        var development = await GroupAsync(admin, "Vývoj");
        var sales = await GroupAsync(admin, "Obchod");
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{space}/access",
            new { groupId = everyone, role = "Reader" })).StatusCode);
        var (member, memberId) = await MemberAsync(site, admin);

        var report = Path.Combine(RepositoryRoot(), "artifacts", "scale", "scale-10k.md");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var seedLog = Path.ChangeExtension(report, ".seed.log");
        File.Delete(seedLog);
        var seeding = Stopwatch.StartNew();
        // site.ExecuteAsync keeps the 30 s default timeout; the seed and its commit (the deferred page triggers: links, mentions) need longer.
        await using (var connection = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(site.ConnectionString) { CommandTimeout = 0 }.ConnectionString))
        {
            await connection.OpenAsync();
            // In transactions: the outbox dispatcher never sees the rows the triggers queue, each batch deletes them before its COMMIT.
            var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in Seed.Split([";\r\n", ";\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var step = Stopwatch.StartNew();
                if (statement.TrimEnd(';') == "COMMIT")
                {
                    await transaction.CommitAsync();
                    await transaction.DisposeAsync();
                    await File.AppendAllTextAsync(seedLog, $"{step.ElapsedMilliseconds,7} ms commit (the deferred page triggers run here)\n");
                    transaction = await connection.BeginTransactionAsync();
                    continue;
                }
                await using var command = new Npgsql.NpgsqlCommand(statement, connection, transaction) { CommandTimeout = 0 };
                foreach (var (name, value) in new[] { ("space", space), ("admin", adminId), ("member", memberId), ("everyone", everyone),
                             ("development", development), ("sales", sales) }.Where(p => statement.Contains("@" + p.Item1)))
                {
                    command.Parameters.AddWithValue(name, value);
                }
                var affected = await command.ExecuteNonQueryAsync();
                var first = statement.Split('\n').Select(l => l.Trim()).First(l => !l.StartsWith("--"));
                await File.AppendAllTextAsync(seedLog, $"{step.ElapsedMilliseconds,7} ms {affected,6} rows  {first[..Math.Min(90, first.Length)]}\n");
            }
            await transaction.DisposeAsync();
        }
        output.WriteLine(await File.ReadAllTextAsync(seedLog));
        // Whatever the deferred triggers queued at commit.
        await site.ExecuteAsync("""DELETE FROM "IndexTask" """);
        output.WriteLine($"Seeded in {seeding.Elapsed.TotalSeconds:F1} s");
        var deep = (Guid)(await site.ScalarAsync("""SELECT "Id" FROM "Page" WHERE "SpaceId"=@space ORDER BY "SortOrder" DESC, "Id" LIMIT 1 OFFSET 5000""", ("space", space)))!;
        var commented = (Guid)(await site.ScalarAsync("""SELECT "ObjectId" FROM "Comment" LIMIT 1"""))!;
        var lastUser = (Guid)(await site.ScalarAsync("""SELECT "Id" FROM "UserAccount" WHERE "Username"='user10000'"""))!;
        using var api = await ApiClientAsync(site, admin, adminId);

        var rows = new List<string>();
        var failures = new List<string>();
        async Task Measure(string who, HttpClient client, string path)
        {
            var shown = path.Replace(space.ToString(), "{space}").Replace(deep.ToString(), "{page}").Replace(commented.ToString(), "{commented}")
                .Replace(everyone.ToString(), "{everyone}").Replace(lastUser.ToString(), "{lastUser}");
            try
            {
                var (cold, _, _, _, _) = await TimeAsync(client, path);
                var (warm, status, bytes, items, sql) = await TimeAsync(client, path);
                rows.Add($"| {who} | `{shown}` | {(int)status} | {items} | {bytes:N0} | {cold} | {warm} | {sql?.ToString() ?? "–"} |");
                if (!((int)status is >= 200 and < 300))
                {
                    failures.Add($"{who} {path}: {(int)status}");
                }
            }
            catch (TaskCanceledException)
            {
                rows.Add($"| {who} | `{shown}` | timeout | – | – | > {client.Timeout.TotalMilliseconds:F0} | – | – |");
                failures.Add($"{who} {path}: no answer within {client.Timeout}");
            }
            // Written after every call: a run stopped by the hang timeout still leaves what it measured.
            await File.WriteAllTextAsync(report, Report(rows, seeding.Elapsed));
        }

        foreach (var (who, client) in new[] { ("admin", admin), ("member", member) })
        {
            await Measure(who, client, $"/api/spaces/{space}/tree");
            await Measure(who, client, "/api/search?q=dokumentace&limit=50");
            // P15: the duplicate check while a title is typed, over 10 000 titles of six shapes (worst case: many near matches).
            await Measure(who, client, $"/api/search/similar-pages?title=Projekt%202.150%20nasazen%C3%AD&spaceId={space}");
            await Measure(who, client, $"/api/spaces/{space}/contents");
            await Measure(who, client, "/api/entities/contact");
            await Measure(who, client, "/api/contacts/duplicates");
            await Measure(who, client, "/api/notifications");
            await Measure(who, client, $"/api/objects/{commented}/comments");
        }
        // „Kdo má přístup“ of a page is for those who manage it: a reader gets 403.
        await Measure("admin", admin, $"/api/pages/{deep}/people?limit=500");
        await Measure("admin", admin, "/api/admin/users");
        // Search, offset and id reach the accounts beyond the first 1 000; the summary counts them all.
        await Measure("admin", admin, "/api/admin/users?q=user9999");
        await Measure("admin", admin, "/api/admin/users?offset=9000");
        await Measure("admin", admin, $"/api/admin/users?id={lastUser}");
        await Measure("admin", admin, "/api/admin/users/summary");
        await Measure("admin", admin, "/api/admin/groups");
        await Measure("admin", admin, $"/api/admin/groups/{everyone}/members");
        await Measure("admin", admin, $"/api/admin/groups/{everyone}/members?offset=9000");
        await Measure("admin", admin, $"/api/access/resources/space/{space}");
        await Measure("admin", admin, $"/api/access/resources/space/{space}?view=direct");
        await Measure("admin (token)", api, $"/api/v1/spaces/{space}/pages");
        await Measure("admin (token)", api, $"/api/v1/spaces/{space}/pages?limit=100&offset=9900");

        // P15 on the server: the graph's backfill of every page (the worker's work after the deploy), then the overview, recognition and a
        // link-only change of one page (the owner's AI run over the wiki is these calls, page by page).
        var backfill = Stopwatch.StartNew();
        var extracted = 0;
        for (int done; (done = await SweepAsync(site)) > 0;)
        {
            extracted += done;
            await File.AppendAllTextAsync(seedLog, $"{backfill.ElapsedMilliseconds,7} ms sweep, {extracted} sources" + Environment.NewLine);
            if (backfill.Elapsed > TimeSpan.FromSeconds(150))
            {
                failures.Add($"sweep: only {extracted} sources in {backfill.Elapsed}");
                break;
            }
        }
        rows.Add($"| system | sweep: {extracted:N0} sources extracted (worker batches of {ContentReferenceStore.SweepBatch}) | – | – | – | {backfill.ElapsedMilliseconds} | – | – |");
        var named = (Guid)(await site.ScalarAsync("""SELECT "PageId" FROM "PageRevision" WHERE "ContentJson" LIKE '%Volal Kontakt%' ORDER BY "PageId" LIMIT 1"""))!;
        await Measure("admin", admin, $"/api/spaces/{space}/unlinked");
        await Measure("member", member, $"/api/spaces/{space}/unlinked");
        await Measure("admin (token)", api, $"/api/v1/spaces/{space}/unlinked?limit=200");
        await MeasurePost("admin (token)", api, "/api/v1/recognize", new { spaceId = space, text = "Volal Kontakt 52, pak Kontakt 4242 a Jana Novák 8 kvůli Projekt 4.120." });
        await MeasurePost("admin (token)", api, "/api/v1/recognize", new { pageId = named });
        // Cold: the link-only write (a new revision, published); warm: the call again at the new version, which links nothing twice.
        var current = JsonNode.Parse(await admin.GetStringAsync($"/api/pages/{named}"))!["version"]!.GetValue<int>();
        var linked = await MeasurePost("admin", admin, $"/api/pages/{named}/link", new { expectedVersion = current }, body => new { expectedVersion = body!["version"]!.GetValue<int>() });
        Assert.True(linked?["published"]?.GetValue<bool>() == true, linked?.ToJsonString());

        // Reach: the accounts beyond the 1 000 the list shows are found, paged to and counted.
        async Task<JsonNode?> MeasurePost(string who, HttpClient client, string path, object body, Func<JsonNode?, object>? again = null)
        {
            var times = new List<long>();
            JsonNode? first = null;
            var status = HttpStatusCode.OK;
            var last = "";
            for (var i = 0; i < 2; i++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await AtlasHttpSite.SendAsync(client, HttpMethod.Post, path, i == 1 && again is not null ? again(first) : body);
                var text = await response.Content.ReadAsStringAsync();
                times.Add(watch.ElapsedMilliseconds);
                first ??= response.IsSuccessStatusCode ? JsonNode.Parse(text) : null;
                status = response.StatusCode;
                last = text;
            }
            rows.Add($"| {who} | `POST {path.Replace(space.ToString(), "{space}")}` | {(int)status} | {(first is null ? "–" : Items(System.Text.Encoding.UTF8.GetBytes(first.ToJsonString())))} | – | {times[0]} | {times[1]} | – |");
            if (!((int)status is >= 200 and < 300))
            {
                failures.Add($"{who} POST {path}: {(int)status} {last}");
            }
            await File.WriteAllTextAsync(report, Report(rows, seeding.Elapsed));
            return first;
        }

        async Task<JsonNode> Read(string path) => JsonNode.Parse(await admin.GetStringAsync(path))!;
        Assert.Equal("user9999", Assert.Single((await Read("/api/admin/users?q=user9999")).AsArray())!["username"]!.GetValue<string>());
        Assert.Equal(1000, (await Read("/api/admin/users?offset=9000")).AsArray().Count);
        Assert.Equal(lastUser, Assert.Single((await Read($"/api/admin/users?id={lastUser}")).AsArray())!["id"]!.GetValue<Guid>());
        Assert.Equal(Count + 2, (await Read("/api/admin/users/summary"))["total"]!.GetValue<int>());
        Assert.Equal(1000, (await Read($"/api/admin/groups/{everyone}/members?offset=9000")).AsArray().Count);

        output.WriteLine(Report(rows, seeding.Elapsed));
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static string Report(List<string> rows, TimeSpan seeding)
    {
        var table = new StringBuilder()
            .AppendLine($"# Atlas list endpoints at {Count:N0} pages, accounts and contacts")
            .AppendLine()
            .AppendLine($"Measured {DateTimeOffset.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName} (in-process TestServer, PostgreSQL from ATLAS_TEST_CONNECTION); " +
                        $"seeding took {seeding.TotalSeconds:F1} s. Items: the length of each top-level array in the answer. SQL: statements of the warm call (Server-Timing).")
            .AppendLine()
            .AppendLine("| as | request | status | items | bytes | cold ms | warm ms | SQL |")
            .AppendLine("|---|---|---|---|---|---|---|---|");
        rows.ForEach(row => table.AppendLine(row));
        return table.ToString();
    }

    private static async Task<(long Ms, HttpStatusCode Status, long Bytes, string Items, int? Sql)> TimeAsync(HttpClient client, string path)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsByteArrayAsync();
        watch.Stop();
        var sql = response.Headers.TryGetValues("Server-Timing", out var timing)
            ? Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", timing)) : null;
        return (watch.ElapsedMilliseconds, response.StatusCode, body.LongLength, Items(body), sql);
    }

    /// <summary>The length of the answer if it is an array, else of each array property: "users=1000".</summary>
    private static string Items(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body) switch
            {
                JsonArray array => array.Count.ToString(),
                JsonObject obj => string.Join(", ", obj.Where(p => p.Value is JsonArray).Select(p => $"{p.Key}={((JsonArray)p.Value!).Count}")) is { Length: > 0 } s ? s : "–",
                _ => "–"
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return "–";
        }
    }

    private static async Task<int> SweepAsync(AtlasHttpSite site)
    {
        await using var scope = site.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<int>(new SweepContentReferences());
    }

    private static async Task<Guid> GroupAsync(HttpClient admin, string name) =>
        (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/groups", new { name }), HttpStatusCode.OK))["id"]!.GetValue<Guid>();

    /// <summary>An ordinary builtin account, activated and signed in; the seed puts it in every group.</summary>
    private static async Task<(HttpClient, Guid)> MemberAsync(AtlasHttpSite site, HttpClient admin)
    {
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = "clen", displayName = "Člen Týmu", isWikiAdmin = false }), HttpStatusCode.Created);
        var client = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = created["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = "clen", password = site.Password }), HttpStatusCode.OK);
        return (client, created["user"]!["id"]!.GetValue<Guid>());
    }

    /// <summary>A personal API token of the administrator for /api/v1.</summary>
    private static async Task<HttpClient> ApiClientAsync(AtlasHttpSite site, HttpClient admin, Guid adminId)
    {
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/admin/users/{adminId}/api-access",
            new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Scale", scopes = new[] { "read" }, expiresInDays = 1 }), HttpStatusCode.Created);
        var client = site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        return client;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Atlas.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Atlas.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>One batch on one connection (the temp table lives in it). Levels go in one by one: the page trigger checks the parent.</summary>
    private const string Seed = """
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp","Email")
        SELECT gen_random_uuid(), 'user' || i, (ARRAY['Jana','Petr','Eva','Tomáš','Lucie','Martin','Kateřina','Jakub'])[1 + i % 8] || ' Novák ' || i,
               false, true, md5(i::text), 'user' || i || '@example.com'
        FROM generate_series(1, 10000) i;

        INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
        SELECT gen_random_uuid(), @everyone, u."Id", now() FROM "UserAccount" u WHERE u."Username" LIKE 'user%' OR u."Id" = @member;
        INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
        SELECT gen_random_uuid(), @development, u."Id", now() FROM "UserAccount" u WHERE u."Username" LIKE 'user%' AND substring(u."Username", 5)::int % 5 = 0 OR u."Id" = @member;
        INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
        SELECT gen_random_uuid(), @sales, u."Id", now() FROM "UserAccount" u WHERE u."Username" LIKE 'user%' AND substring(u."Username", 5)::int % 20 = 1 OR u."Id" = @member;

        -- 50 + 500 + 3 000 + 6 450 = 10 000 pages: 10 children per top-level page, 6 per second-level, 3 or 2 per third-level.
        CREATE TEMP TABLE scale_page (id uuid PRIMARY KEY, revision uuid NOT NULL, parent uuid, depth int NOT NULL, n int NOT NULL, ord int NOT NULL);
        INSERT INTO scale_page SELECT gen_random_uuid(), gen_random_uuid(), NULL, 1, i, i FROM generate_series(1, 50) i;
        INSERT INTO scale_page SELECT gen_random_uuid(), gen_random_uuid(), p.id, 2, row_number() OVER (), c FROM scale_page p, generate_series(1, 10) c WHERE p.depth = 1;
        INSERT INTO scale_page SELECT gen_random_uuid(), gen_random_uuid(), p.id, 3, row_number() OVER (), c FROM scale_page p, generate_series(1, 6) c WHERE p.depth = 2;
        INSERT INTO scale_page SELECT gen_random_uuid(), gen_random_uuid(), p.id, 4, row_number() OVER (), c FROM scale_page p, generate_series(1, 3) c
          WHERE p.depth = 3 AND (c < 3 OR p.n <= 450);
        INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version","SortOrder") SELECT id, @space, parent, @admin, 1, ord * 1024 FROM scale_page WHERE depth = 1;
        INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version","SortOrder") SELECT id, @space, parent, @admin, 1, ord * 1024 FROM scale_page WHERE depth = 2;
        INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version","SortOrder") SELECT id, @space, parent, @admin, 1, ord * 1024 FROM scale_page WHERE depth = 3;
        INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version","SortOrder") SELECT id, @space, parent, @admin, 1, ord * 1024 FROM scale_page WHERE depth = 4;
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
        SELECT revision, id, 1, title,
               '{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"' || body || '"}]}]}}',
               body, @admin, now(), 'web'
        FROM scale_page, LATERAL (SELECT (ARRAY['Projekt','Postup','Návod','Zápis','Specifikace','Přehled'])[1 + n % 6] || ' ' || depth || '.' || n AS title,
               'Interní dokumentace týmu: postup nasazení, kontakty, odpovědnosti a poznámky k části ' || depth || '.' || n || '. Stránka popisuje provoz, zálohy a schvalování změn.' AS body) t;
        INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel") SELECT gen_random_uuid(), id, revision, @admin, now(), 'web' FROM scale_page;
        UPDATE "Page" p SET "PublishedRevisionId" = s.revision FROM scale_page s WHERE p."Id" = s.id;
        -- P15: a tenth of the deepest pages name people in plain text: a contact (some names two contacts) and an account.
        UPDATE "PageRevision" r SET "ContentJson" = replace(r."ContentJson", 'Interní dokumentace týmu', 'Volal Kontakt ' || (2 + s.n % 4000 * 2) || ' a Jana Novák 8 kvůli dokumentaci')
        FROM scale_page s WHERE r."Id" = s.revision AND s.depth = 4 AND s.n % 10 = 0;
        -- The pages commit before the contacts exist: with 10 000 contacts present, each deferred atlas_refresh_page_object_mentions took ~12 ms.
        COMMIT;

        -- Contacts: every 50th shares its name with the next one, so the duplicate list has pairs to find.
        CREATE TEMP TABLE scale_contact AS
          SELECT gen_random_uuid() AS id, i, 'Kontakt ' || (CASE WHEN i % 50 = 1 THEN i + 1 ELSE i END) AS name, 'kontakt' || i || '@example.com' AS email
          FROM generate_series(1, 10000) i;
        INSERT INTO "ContentResource"("Id","TypeId","SpaceId","CreatedAt","Scope","Version","CreatedBy","UpdatedAt","Title","Data")
        SELECT id, 'atlas.contact', NULL, now() - make_interval(secs => 10000 - i), 'wiki', 1, @admin, now(), name,
               jsonb_build_object('name', name, 'emails', jsonb_build_array(email))
        FROM scale_contact;
        INSERT INTO "ObjectChange"("ObjectId","Version","ChangeId","AuthorId","AppliedAt","PayloadJson")
        SELECT id, 1, id, @admin, now(), jsonb_build_object('name', name, 'emails', jsonb_build_array(email)) FROM scale_contact;
        INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes")
        SELECT id, 'published', 'contact', 'email:' || email, jsonb_build_object('name', name, 'emails', jsonb_build_array(email), 'objectId', id) FROM scale_contact;

        -- 300 comments on one second-level page; 300 page mentions in the member's inbox, 100 of them read.
        INSERT INTO "Comment"("Id","ObjectId","AuthorId","Body","CreatedAt")
        SELECT gen_random_uuid(), (SELECT id FROM scale_page WHERE depth = 2 ORDER BY n LIMIT 1), CASE WHEN i % 2 = 0 THEN @admin ELSE @member END,
               'Komentář ' || i || ': prosím o kontrolu postupu.', now() - make_interval(mins => 300 - i)
        FROM generate_series(1, 300) i;
        INSERT INTO "Notification"("Id","RecipientId","Kind","SubjectId","AggregationKey","ActorId","CreatedAt","UpdatedAt","ReadAt")
        SELECT gen_random_uuid(), @member, 'page.mention', s.id, 'page.mention:' || s.id, @admin, now(), now() - make_interval(mins => s.n),
               CASE WHEN s.n % 3 = 0 THEN now() END
        FROM scale_page s WHERE s.depth = 3 AND s.n <= 300;

        ANALYZE "UserAccount", "UserAccountRevision", "GroupMember", "Page", "PageRevision", "PagePublication", "PageOrganizationRevision",
          "SearchDocument", "WorkingSearchDocument", "ContentResource", "EntityOccurrence", "ObjectChange", "Comment", "Notification";
        COMMIT;
        """;
}
