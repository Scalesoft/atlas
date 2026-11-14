using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

public sealed class ActivitySummaryTests
{
    private const string Json = "{}";
    private const string WholeInstallation = "the summary counts every revision, page opening and tool call of the installation, which other tests add meanwhile";

    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_reads_it_and_days_must_be_in_range()
    {
        await using var f = await AccessAuditFixture.CreateAsync(WholeInstallation);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<ActivitySummary>(f.Member, new GetActivitySummary(null)))).Status);
        foreach (var days in new[] { 0, -1, 3651 })
        {
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<ActivitySummary>(f.Admin, new GetActivitySummary(days)))).Status);
        }
        var empty = await f.As<ActivitySummary>(f.Admin, new GetActivitySummary(null));
        Assert.Null(empty.From);
        Assert.Equal((0, 0, 0), (empty.Revisions, empty.ToolCalls, empty.ToolFailures));
    }

    [PostgreSqlFact]
    public async Task Counts_channels_top_lists_and_failures_follow_the_rows_and_the_days_window()
    {
        await using var f = await AccessAuditFixture.CreateAsync(WholeInstallation);
        var (admin, member) = (f.Admin.Id, f.Member.Id);
        var sql = new System.Text.StringBuilder($"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('00000000-0000-0000-0000-0000000000a1','{f.SpaceId}','{admin}',1);
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ExpiresAt")
              VALUES('00000000-0000-0000-0000-0000000000b1','{admin}','T','{new string('A', 64)}','atl_test','stamp','2026-01-01T00:00:00Z','2099-01-01T00:00:00Z');
            """);
        var version = 0;
        void Revision(Guid author, string channel, bool ai, int daysAgo) => sql.Append($"""
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel","ApiTokenId","AiAssisted")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000a1',{++version},'t','{Json}','m','{author}',now()-interval '{daysAgo} days',now()-interval '{daysAgo} days'+interval '1 second','{channel}',
              {(channel == "mcp" ? "'00000000-0000-0000-0000-0000000000b1'" : "NULL")},{(ai ? "true" : "false")});

            """);
        void Open(string channel, int daysAgo) => sql.Append($"""
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action","Channel","ApiTokenId","OccurredAt")
              VALUES('00000000-0000-0000-0000-0000000000a1',(SELECT "Id" FROM "PageRevision" LIMIT 1),'{admin}','page.opened',{(channel == "" ? "NULL" : $"'{channel}'")},{(channel == "mcp" ? "'00000000-0000-0000-0000-0000000000b1'" : "NULL")},now()-interval '{daysAgo} days');

            """);
        void Activity(string channel, string operation, string outcome, int daysAgo, Guid? actor = null) => sql.Append($"""
            INSERT INTO "ApiActivity"("ActorId","TokenId","Channel","Operation","Outcome","OccurredAt")
              VALUES('{actor ?? admin}',{(channel == "assistant" ? "NULL" : "'00000000-0000-0000-0000-0000000000b1'")},'{channel}','{operation}','{outcome}',now()-interval '{daysAgo} days');

            """);
        Revision(admin, "web", false, 1);
        Revision(admin, "web", true, 2);
        Revision(member, "web", false, 3);
        Revision(admin, "mcp", true, 4);
        Revision(member, "legacy", false, 400);
        Open("web", 1); Open("web", 2); Open("mcp", 1); Open("", 1); Open("web", 400);
        Activity("mcp", "atlas_publish", "ok", 1);
        Activity("mcp", "atlas_publish", "not_found", 1);
        Activity("assistant", "assistant.proposal", "pending", 1);
        Activity("assistant", "assistant.decision", "applied", 1);
        Activity("assistant", "assistant.decision", "declined", 1);
        Activity("assistant", "assistant.decision", "invalid", 1, member);
        Activity("mcp", "atlas_publish", "scope_missing", 400);
        await f.Execute(sql.ToString(), null);

        var all = await f.As<ActivitySummary>(f.Admin, new GetActivitySummary(null));
        Assert.Equal((5, 2), (all.Revisions, all.AiAssistedRevisions));
        Assert.Equal(new[] { ("web", 3), ("legacy", 1), ("mcp", 1) }, all.RevisionChannels.Select(x => (x.Key, x.Count)));
        Assert.Equal(new[] { f.Admin.Id, f.Member.Id }, all.TopEditors.Select(x => x.UserId));
        Assert.Equal(new[] { 3, 2 }, all.TopEditors.Select(x => x.Count));
        Assert.Equal(new[] { ("web", 3), ("mcp", 1) }, all.PageOpens.Select(x => (x.Key, x.Count)));
        Assert.Equal((7, 3), (all.ToolCalls, all.ToolFailures));
        Assert.Equal(new[] { ("assistant", 4), ("mcp", 3) }, all.ToolChannels.Select(x => (x.Key, x.Count)));
        Assert.Equal(("assistant", "assistant.decision", 3, 1), (all.TopTools[0].Channel, all.TopTools[0].Operation, all.TopTools[0].Calls, all.TopTools[0].Failures));
        Assert.Equal(("mcp", "atlas_publish", 3, 2), (all.TopTools[1].Channel, all.TopTools[1].Operation, all.TopTools[1].Calls, all.TopTools[1].Failures));
        Assert.Equal(new[] { 6, 1 }, all.TopToolUsers.Select(x => x.Count));

        var week = await f.As<ActivitySummary>(f.Admin, new GetActivitySummary(7));
        Assert.NotNull(week.From);
        Assert.Equal((4, 2, 3, 6, 2), (week.Revisions, week.AiAssistedRevisions, week.PageOpens.Sum(x => x.Count), week.ToolCalls, week.ToolFailures));
        Assert.DoesNotContain(week.RevisionChannels, x => x.Key == "legacy");
        Assert.Equal(2, week.TopTools.Single(x => x.Channel == "mcp").Calls);
    }
}
