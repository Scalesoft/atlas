using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.PageVerifications;
using Atlas.Server.Features.PageVerifications.Commands;
using Atlas.Server.Features.PageVerifications.Domain;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.PageVerifications;

/// <summary>5.2 Ověřený obsah (docs/atlas-5.2-plan.md): the state's boundaries, the rights of each action by role, the reads that carry the
/// verification (bundle, Stav obsahu, MCP) and the job's one notice per stage.</summary>
public sealed class PageVerificationTests
{
    [Fact]
    public void The_state_changes_exactly_at_its_boundaries()
    {
        var now = new DateTime(2026, 11, 24, 5, 15, 0, DateTimeKind.Utc);
        Assert.Equal(PageVerificationStates.Off, PageVerificationStates.Of(null, now.AddYears(1), now));
        Assert.Equal(PageVerificationStates.Off, PageVerificationStates.Of(0, now.AddYears(1), now));
        Assert.Equal(PageVerificationStates.Unverified, PageVerificationStates.Of(6, null, now));
        Assert.Equal(PageVerificationStates.Expired, PageVerificationStates.Of(6, now, now));
        Assert.Equal(PageVerificationStates.Expiring, PageVerificationStates.Of(6, now.AddTicks(1), now));
        Assert.Equal(PageVerificationStates.Expiring, PageVerificationStates.Of(6, now.AddDays(14), now));
        Assert.Equal(PageVerificationStates.Verified, PageVerificationStates.Of(6, now.AddDays(14).AddTicks(1), now));
        // The page's own interval wins, the space's applies without one, and a template is never verified.
        Assert.Equal(0, PageVerificationStates.Interval(0, 6, false));
        Assert.Equal(6, PageVerificationStates.Interval(null, 6, false));
        Assert.Null(PageVerificationStates.Interval(3, 6, true));
    }

    [PostgreSqlFact]
    public async Task Each_action_follows_the_page_rights_and_the_reads_carry_the_verification()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        await f.Site.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=@space""", ("space", Guid.Parse(space)));
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var (reader, readerId) = await f.UserAsync("reader");
        await f.GrantAsync(space, readerId, "Reader");
        var (writer, writerId) = await f.UserAsync("writer");
        await f.GrantAsync(space, writerId, "Writer");
        var (stranger, strangerId) = await f.UserAsync("stranger");
        var url = $"/api/pages/{page.Id}/verification";
        var health = $"/api/spaces/{space}/content-health";

        // A new page of a verifying space: its author is the steward, unverified until someone verifies it.
        var created = await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK);
        Assert.Equal((PageVerificationStates.Unverified, f.AdminId), (created["state"]!.GetValue<string>(), created["stewardId"]!.GetValue<string>()));

        // Reader: reads it and reports, never changes it nor sees Stav obsahu. Without access the page does not exist.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Put, url, new { verify = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Put, url, new { stewardId = readerId })).StatusCode);
        Assert.Equal(1, (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, url + "/report", new { note = "Staré číslo." }),
            HttpStatusCode.OK))["notified"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, url + "/report", new { note = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(health)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(stranger, HttpMethod.Put, url, new { verify = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(stranger, HttpMethod.Post, url + "/report", new { })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await stranger.GetAsync(health)).StatusCode);

        // Writer: verifies, changes the interval and the steward; the steward must read the page.
        var verified = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { verify = true }), HttpStatusCode.OK);
        Assert.Equal((PageVerificationStates.Verified, page.Version, false, writerId),
            (verified["state"]!.GetValue<string>(), verified["verifiedVersion"]!.GetValue<int>(), verified["verifiedViaAi"]!.GetValue<bool>(),
                verified["verifiedById"]!.GetValue<string>()));
        Assert.Equal(PageVerificationStates.Off, (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { intervalMonths = 0 }),
            HttpStatusCode.OK))["state"]!.GetValue<string>());
        Assert.Equal(PageVerificationStates.Verified, (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { inheritInterval = true }),
            HttpStatusCode.OK))["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { stewardId = strangerId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { intervalMonths = 5 })).StatusCode);
        var expiring = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url,
            new { verify = true, until = DateTime.UtcNow.AddDays(10), stewardId = readerId }), HttpStatusCode.OK);
        Assert.Equal((PageVerificationStates.Expiring, readerId), (expiring["state"]!.GetValue<string>(), expiring["stewardId"]!.GetValue<string>()));

        // The page view's bundle carries the state and the panel's readership; Stav obsahu lists it among the due pages.
        var bundle = (await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{page.Id}?bundle=true"), HttpStatusCode.OK))["bundle"]!;
        Assert.Equal(PageVerificationStates.Expiring, bundle["verification"]!["state"]!.GetValue<string>());
        Assert.NotNull(bundle["readers"]);
        var list = await AtlasHttpSite.Json(await writer.GetAsync(health + "?state=due"), HttpStatusCode.OK);
        Assert.Equal(1, list["summary"]!["expiring"]!.GetValue<int>());
        Assert.Equal(page.Id, Assert.Single(list["items"]!.AsArray())!["pageId"]!.GetValue<string>());
        // The tiles of Stav obsahu filter by their own state: the page is expiring, so verified and lapsed are empty.
        Assert.Single((await AtlasHttpSite.Json(await writer.GetAsync(health + "?state=expiring"), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Empty((await AtlasHttpSite.Json(await writer.GetAsync(health + "?state=verified"), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Empty((await AtlasHttpSite.Json(await writer.GetAsync(health + "?state=lapsed"), HttpStatusCode.OK))["items"]!.AsArray());
        // The link chips read the state from the tree and the batch reference check.
        var tree = await AtlasHttpSite.Json(await reader.GetAsync($"/api/spaces/{space}/tree"), HttpStatusCode.OK);
        Assert.Equal(PageVerificationStates.Expiring, tree["items"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == page.Id)!["verification"]!.GetValue<string>());
        var references = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/pages/references", new { ids = new[] { page.Id } }),
            HttpStatusCode.OK);
        Assert.Equal(PageVerificationStates.Expiring, Assert.Single(references["items"]!.AsArray())!["verification"]!.GetValue<string>());

        // Bulk: a writer clears the steward, a page of nobody is skipped with its reason; a reader changes nothing.
        var bulk = $"/api/spaces/{space}/content-health/bulk";
        var cleared = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, bulk,
            new { action = "steward", pageIds = new[] { page.Id, Guid.NewGuid().ToString() }, stewardId = (string?)null }), HttpStatusCode.OK);
        Assert.Equal((1, 1), (cleared["changed"]!.GetValue<int>(), cleared["skipped"]!.AsArray().Count));
        Assert.Null((await AtlasHttpSite.Json(await writer.GetAsync(url), HttpStatusCode.OK))["stewardId"]);
        var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, bulk, new { action = "verify", pageIds = new[] { page.Id } }),
            HttpStatusCode.OK);
        Assert.Equal((0, 1), (refused["changed"]!.GetValue<int>(), refused["skipped"]!.AsArray().Count));

        // Over MCP: verifying needs the write scope, is marked „přes AI“, and atlas_get_page and the content health show it.
        await f.SetLevelAsync(f.AdminId, "write");
        var reading = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"], "Read")).Secret);
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_set_verification", new { pageId = page.Id, verify = true })));
        var writing = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Write")).Secret);
        Assert.True((await CallAsync(writing, "atlas_set_verification", new { pageId = page.Id, verify = true })).IsError != true);
        Assert.True((await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK))["verifiedViaAi"]!.GetValue<bool>());
        Assert.Equal(PageVerificationStates.Verified,
            Structured(await CallAsync(reading, "atlas_get_page", new { pageId = page.Id }))["verification"]!["state"]!.GetValue<string>());
        Assert.Equal(1, Structured(await CallAsync(reading, "atlas_list_content_health", new { spaceId = space }))["summary"]!["verified"]!.GetValue<int>());
    }

    [PostgreSqlFact]
    public async Task The_job_tells_the_steward_once_per_stage_however_often_it_runs()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", f.SpaceId);
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Postup", Atlas.Server.Tests.Attachments.AttachmentFixture.Rich("Postup"), "Postup")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        var now = DateTime.UtcNow;
        await f.As<PageVerificationView>(f.Writer, new SetPageVerification(page.Id, new SetPageVerificationRequest(Verify: true, Until: now.AddDays(10))));

        // Two runs on one day: one notice „brzy vyprší“; two more after the expiry: the same item, now „vypršelo“, counted once more.
        foreach (var day in new[] { now, now, now.AddDays(11), now.AddDays(11) })
        {
            while (await f.Host.InvokeAsync<int>(new SendVerificationNotices(day)) == SendVerificationNoticesHandler.Batch)
            {
            }
            await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);
            var notices = (await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(f.Writer,
                new Atlas.Server.Features.Notifications.Queries.ListNotifications(null))).Items.Where(x => x.SubjectId == page.Id).ToList();
            var notice = Assert.Single(notices);
            Assert.Equal((day == now ? NotificationKinds.VerificationExpiring : NotificationKinds.VerificationExpired, day == now ? 1 : 2),
                (notice.Kind, notice.Count));
        }
    }
}
