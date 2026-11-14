using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Platform.Events;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Projector;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>3.6-C: the rules that group saved revisions into versions („Pravidla slučování“) and the change summary of a version.</summary>
public sealed class RevisionVersionsTests
{
    private static readonly Guid Ann = Guid.NewGuid(), Bob = Guid.NewGuid(), Token = Guid.NewGuid();
    // 2026-11-03 10:00 in Prague (CET, UTC+1).
    private static readonly DateTime Morning = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private static PageRevision Revision(DateTime at, Guid author, string channel = "web", Guid? token = null, DateTime? started = null) => new()
    {
        Id = Guid.NewGuid(), ActiveFrom = at, AuthorId = author, Channel = channel, ApiTokenId = token, GroupStartedAt = started ?? at
    };

    [Fact]
    public void People_share_a_version_until_a_pause_the_longest_version_or_midnight()
    {
        var previous = Revision(Morning, Ann);
        Assert.True(RevisionVersions.Continues(previous, Revision(Morning.AddMinutes(29), Bob)));
        Assert.False(RevisionVersions.Continues(previous, Revision(Morning.AddMinutes(31), Ann)));
        var longVersion = Revision(Morning.AddHours(3.9), Ann, started: Morning);
        Assert.True(RevisionVersions.Continues(longVersion, Revision(Morning.AddHours(3.95), Ann)));
        Assert.False(RevisionVersions.Continues(longVersion, Revision(Morning.AddHours(4.05), Ann)));
        // 23:50 and 00:05 in Prague are 22:50 and 23:05 UTC: another day there, the same one in UTC.
        var evening = Revision(new DateTime(2026, 11, 3, 22, 50, 0, DateTimeKind.Utc), Ann);
        Assert.False(RevisionVersions.Continues(evening, Revision(new DateTime(2026, 11, 3, 23, 5, 0, DateTimeKind.Utc), Ann)));
        Assert.True(RevisionVersions.Continues(Revision(Morning, Ann, "system"), Revision(Morning.AddMinutes(1), Ann, "system")));
        Assert.False(RevisionVersions.Continues(Revision(Morning, Ann, "system"), Revision(Morning.AddMinutes(1), Ann)));
    }

    [Fact]
    public void A_change_with_AI_is_a_version_of_its_own_joined_only_by_its_source_within_ten_minutes()
    {
        var human = Revision(Morning, Ann);
        var mcp = Revision(Morning.AddMinutes(1), Ann, "mcp", Token);
        Assert.True(RevisionVersions.IsAi(mcp));
        Assert.False(RevisionVersions.Continues(human, mcp));
        Assert.False(RevisionVersions.Continues(mcp, Revision(Morning.AddMinutes(2), Ann)));
        Assert.True(RevisionVersions.Continues(mcp, Revision(Morning.AddMinutes(9), Ann, "mcp", Token)));
        Assert.False(RevisionVersions.Continues(mcp, Revision(Morning.AddMinutes(12), Ann, "mcp", Token)));
        Assert.False(RevisionVersions.Continues(mcp, Revision(Morning.AddMinutes(2), Ann, "mcp", Guid.NewGuid())));
        Assert.False(RevisionVersions.Continues(mcp, Revision(Morning.AddMinutes(2), Ann, "api", Token)));
        var assistant = Revision(Morning, Ann, "assistant");
        assistant.AiConversationId = Guid.NewGuid();
        var otherConversation = Revision(Morning.AddMinutes(1), Ann, "assistant");
        otherConversation.AiConversationId = Guid.NewGuid();
        Assert.False(RevisionVersions.Continues(assistant, otherConversation));
        var marked = Revision(Morning.AddMinutes(1), Ann);
        marked.AiAssisted = true;
        Assert.False(RevisionVersions.Continues(human, marked));
    }

    [Fact]
    public void The_summary_counts_lines_blocks_sections_requirements_contacts_and_the_title()
    {
        var before = Snapshot("Plán", "# Úvod\n\nStarý text\n\n# Nasazení\n\nBeze změny",
            ProjectorDocuments.Heading("Úvod", "a"), ProjectorDocuments.Paragraph("Starý text"),
            ProjectorDocuments.Block("b1", "db", "Databáze"), ProjectorDocuments.Block("b2", "web", "Web"), Contact("Jana Nová"));
        var after = Snapshot("Plán nasazení", "# Úvod\n\nNový text\nDalší řádek\n\n# Nasazení\n\nBeze změny",
            ProjectorDocuments.Heading("Úvod", "a"), ProjectorDocuments.Paragraph("Nový text"),
            ProjectorDocuments.Block("b1", "db", "Databáze", state: "done"), ProjectorDocuments.Block("b3", "api", "API"), Contact("Petr Malý"));

        var changes = RevisionVersions.Summarize(before, after);

        Assert.Equal((2, 1), (changes.Added, changes.Removed));
        Assert.Equal(["Úvod"], changes.Sections);
        Assert.Equal(4, changes.Blocks);
        Assert.Equal(new RequirementStateChange("Databáze", "planned", "done", "b1"), Assert.Single(changes.States));
        // Touched: the changed block and the new one; against the revision right before when that is not the version's start.
        Assert.Equal(["b1", "b3"], changes.Touched);
        Assert.Equal(["b3"], RevisionVersions.Summarize(before, after, after.ContentJson.Replace("\"b3\"", "\"b4\"")).Touched);
        Assert.Equal((1, 1), (changes.RequirementsAdded, changes.RequirementsRemoved));
        Assert.Equal(["Petr Malý"], changes.ContactsAdded);
        Assert.Equal(["Jana Nová"], changes.ContactsRemoved);
        Assert.Equal("Plán", changes.TitleBefore);
        Assert.False(RevisionVersions.IsLarge(before.Markdown, after.Markdown));
    }

    [Fact]
    public void A_reordered_list_changes_no_line_but_names_its_section()
    {
        static JsonObject List(params string[] items) => new()
        {
            ["type"] = "bulletList",
            ["content"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(ProjectorDocuments.Paragraph(item)) }).ToArray())
        };
        var before = Snapshot("T", "## Postup\n\n- a\n- b", ProjectorDocuments.Heading("Postup", "p"), List("a", "b"));
        var after = Snapshot("T", "## Postup\n\n- b\n- a", ProjectorDocuments.Heading("Postup", "p"), List("b", "a"));

        var changes = RevisionVersions.Summarize(before, after);

        Assert.Equal((0, 0, 1), (changes.Added, changes.Removed, changes.Blocks));
        Assert.Equal(["Postup"], changes.Sections);
    }

    [Fact]
    public void A_rewrite_of_more_than_half_of_a_page_is_a_large_change_a_growing_short_page_is_not()
    {
        var lines = Enumerable.Range(0, 40).Select(i => "Řádek " + i).ToArray();
        var rewritten = lines.Select((line, i) => i < 25 ? "Nový " + i : line).ToArray();
        Assert.True(RevisionVersions.IsLarge(string.Join('\n', lines), string.Join('\n', rewritten)));
        Assert.False(RevisionVersions.IsLarge("Jedna", "Jedna\nDvě\nTři"));
        // A heading inside a code fence is no section.
        var fenced = RevisionVersions.Summarize(Snapshot("T", "# A\n```\n# B\nx\n```"), Snapshot("T", "# A\n```\n# B\ny\n```"));
        Assert.Equal(["A"], fenced.Sections);
    }

    /// <summary>Migration 2026110630 over an installation's revisions: each becomes a version of its own measured against the revision
    /// before it, without a summary.</summary>
    [PostgreSqlFact]
    public async Task The_migration_makes_every_existing_revision_a_version_of_its_own()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_revision_groups_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110630);
        const string Owner = "00000000-0000-0000-0000-000000000001", Page = "00000000-0000-0000-0000-0000000000a1";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',2,1024);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000b1','{Page}',1,'A','{"{"}{"}"}','a','{Owner}','2026-10-01T08:00:00Z','2026-10-01T09:00:00Z','web'),
              ('00000000-0000-0000-0000-0000000000b2','{Page}',2,'B','{"{"}{"}"}','b','{Owner}','2026-10-01T09:00:00Z',NULL,'import');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("b1:b1:2026-10-01 08:00:-:1:f:-:-|b2:b2:2026-10-01 09:00:b1:1:f:-:-", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right(CAST("Id" AS text), 2) || ':' || right(CAST("GroupId" AS text), 2) || ':'
              || to_char("GroupStartedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ':' || coalesce(right(CAST("BaseRevisionId" AS text), 2), '-')
              || ':' || "CheckpointCount" || ':' || CASE WHEN "IsExplicit" THEN 't' ELSE 'f' END || ':' || coalesce("Label", '-')
              || ':' || coalesce(CAST("ChangeSummary" AS text), '-'), '|' ORDER BY "Version")
            FROM "PageRevision"
            """));
    }

    /// <summary>Migration 2026111020 over existing revisions: none names a revision it equals, so every existing version stays listed.</summary>
    [PostgreSqlFact]
    public async Task The_same_as_migration_keeps_every_existing_version_listed()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_revision_same_as_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026111020);
        const string Owner = "00000000-0000-0000-0000-000000000001", Page = "00000000-0000-0000-0000-0000000000a1";
        // Version 3 returned to version 1's text before the column existed: it stays a version of its own.
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',3,1024);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel","GroupId","BaseRevisionId") VALUES
              ('00000000-0000-0000-0000-0000000000b1','{Page}',1,'A','{"{"}{"}"}','a','{Owner}','2026-10-01T08:00:00Z','2026-10-01T09:00:00Z','web','00000000-0000-0000-0000-0000000000b1',NULL),
              ('00000000-0000-0000-0000-0000000000b2','{Page}',2,'A','{"{"}{"}"}','ax','{Owner}','2026-10-01T09:00:00Z','2026-10-01T09:01:00Z','web','00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-0000000000b1'),
              ('00000000-0000-0000-0000-0000000000b3','{Page}',3,'A','{"{"}{"}"}','a','{Owner}','2026-10-01T09:01:00Z',NULL,'web','00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-0000000000b1');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("b1:b1:-|b2:b2:-|b3:b2:-", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right(CAST("Id" AS text), 2) || ':' || right(CAST("GroupId" AS text), 2) || ':'
              || coalesce(right(CAST("SameAsRevisionId" AS text), 2), '-'), '|' ORDER BY "Version")
            FROM "PageRevision"
            """));
    }

    /// <summary>A closed version is announced once, with why, for automatic publication (3.6-I): the save that opens the next version
    /// for an AI boundary, „Uložit verzi“ at once, and a pause by the check scheduled after the last checkpoint.</summary>
    [PostgreSqlFact]
    public async Task A_closed_version_is_announced_once_with_its_reason()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_version_closed_", outbox: true);
        host.Actor.Current = await host.SeedUserAsync("writer", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("Plány");
        static string Rich(string text) => ProjectorDocuments.Json(ProjectorDocuments.Paragraph(text));
        var page = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "A", Rich("A"), null)))).Id;
        await host.InvokeAsync<PageDto>(new SavePage(page, new SavePageRequest("B", Rich("B"), null, 1)));
        await host.InvokeAsync<PageDto>(new SavePage(page, new SavePageRequest("C", Rich("C"), null, 2, AiAssisted: true)));
        await host.InvokeAsync<NamedVersion>(new NameVersion(page, 3, null));
        await host.InvokeAsync<PageDto>(new SavePage(page, new SavePageRequest("D", Rich("D"), null, 3)));
        Assert.True((long)(await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM wolverine_incoming_envelopes WHERE received_at = '{Outbox.QueueUri}' AND status = 'Scheduled'
            """))! >= 3);
        Guid[] revisions;
        using (var session = host.OpenSession())
        {
            revisions = [.. await session.QueryOver<PageRevision>().Where(x => x.PageId == page).OrderBy(x => x.Version).Asc.Select(x => x.Id).ListAsync<Guid>()];
        }

        // The checks of revisions that something followed say nothing; the last one's pause closes its version.
        await host.InvokeAsync(new VersionPauseCheck(page, revisions[1]));
        await host.InvokeAsync(new VersionPauseCheck(page, revisions[3]));
        await OutboxTests.DrainAsync(host);

        Assert.Equal(
            [new VersionClosed(page, revisions[1], "ai"), new VersionClosed(page, revisions[2], "named"), new VersionClosed(page, revisions[3], "pause")],
            VersionClosedProbeHandler.Of(page));
    }

    private static RevisionVersions.Snapshot Snapshot(string title, string markdown, params JsonNode[] content)
        => new(title, ProjectorDocuments.Json(content), markdown);

    private static JsonObject Contact(string name) => new()
    {
        ["type"] = "contactCard",
        ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["contacts"] = new JsonArray(new JsonObject { ["name"] = name }) } }
    };

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}

/// <summary>A consumer of VersionClosed, as automatic publication (3.6-I) will be: records every delivery per page.</summary>
public sealed record VersionClosedProbe(VersionClosed Event) : IOutboxDelivery<VersionClosed>
{
    public static bool Takes(VersionClosed message) => true;
}

public static class VersionClosedProbeHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<VersionClosed>> Deliveries = new();

    public static void Handle(VersionClosedProbe delivery) => Deliveries.GetOrAdd(delivery.Event.PageId, _ => new()).Enqueue(delivery.Event);

    public static VersionClosed[] Of(Guid pageId) => Deliveries.TryGetValue(pageId, out var seen) ? seen.ToArray() : [];
}
