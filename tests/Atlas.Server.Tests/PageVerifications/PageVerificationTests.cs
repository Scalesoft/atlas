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
using Npgsql;
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

    [Fact]
    public void A_date_without_a_time_is_verified_through_that_day_in_Prague()
    {
        // Winter (UTC+1) and summer (UTC+2): the last microsecond of the day there, so the chip still shows that day.
        var winter = PageVerificationHandler.Until(new DateTime(2027, 3, 1));
        Assert.Equal(new DateTime(2027, 3, 1, 23, 0, 0, DateTimeKind.Utc).AddTicks(-10), winter);
        Assert.Equal(new DateTime(2027, 7, 1, 22, 0, 0, DateTimeKind.Utc).AddTicks(-10), PageVerificationHandler.Until(new DateTime(2027, 7, 1)));
        Assert.Equal(PageVerificationStates.Expiring, PageVerificationStates.Of(6, winter, new DateTime(2027, 3, 1, 22, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(PageVerificationStates.Expired, PageVerificationStates.Of(6, winter, new DateTime(2027, 3, 1, 23, 0, 0, DateTimeKind.Utc)));
        // A time without a zone is Prague's; one with a zone is taken as it is.
        Assert.Equal(new DateTime(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc), PageVerificationHandler.Until(new DateTime(2027, 3, 1, 10, 0, 0)));
        Assert.Equal(new DateTime(2027, 3, 1, 10, 0, 0, DateTimeKind.Utc), PageVerificationHandler.Until(new DateTime(2027, 3, 1, 10, 0, 0, DateTimeKind.Utc)));
        // 02:30 on the day the clocks go forward does not exist in Prague: 03:30 summer time, not an internal error.
        Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), PageVerificationHandler.Until(new DateTime(2027, 3, 28, 2, 30, 0)));
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
        // The answer carries the panel's Čtenost, so the page does not read its bundle again.
        Assert.NotNull(verified["readers"]);
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
        // The search hit carries the same string (one contract with the tree and the references).
        var hits = await AtlasHttpSite.Json(await reader.GetAsync("/api/search?q=Postup"), HttpStatusCode.OK);
        Assert.Equal(PageVerificationStates.Expiring, hits["items"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == page.Id)!["verification"]!.GetValue<string>());

        // Bulk: a writer clears the steward, a page of nobody is skipped with its reason; a reader is refused.
        var bulk = $"/api/spaces/{space}/content-health/bulk";
        var cleared = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, bulk,
            new { action = "steward", pageIds = new[] { page.Id, Guid.NewGuid().ToString() }, stewardId = (string?)null }), HttpStatusCode.OK);
        Assert.Equal((1, 1), (cleared["changed"]!.GetValue<int>(), cleared["skipped"]!.AsArray().Count));
        Assert.Null((await AtlasHttpSite.Json(await writer.GetAsync(url), HttpStatusCode.OK))["stewardId"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, bulk, new { action = "verify", pageIds = new[] { page.Id } })).StatusCode);

        // Over MCP: verifying needs the write scope, is marked „přes AI“, and atlas_get_page and the content health show it.
        await f.SetLevelAsync(f.AdminId, "write");
        var reading = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"], "Read")).Secret);
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_set_verification", new { pageId = page.Id, verify = true })));
        var writing = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Write")).Secret);
        Assert.True((await CallAsync(writing, "atlas_set_verification", new { pageId = page.Id, verify = true })).IsError != true);
        Assert.True((await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK))["verifiedViaAi"]!.GetValue<bool>());
        // The steward is set and cleared over MCP as on the web.
        Assert.True((await CallAsync(writing, "atlas_set_verification", new { pageId = page.Id, stewardId = readerId })).IsError != true);
        Assert.Equal(readerId, (await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK))["stewardId"]!.GetValue<string>());
        Assert.True((await CallAsync(writing, "atlas_set_verification", new { pageId = page.Id, clearSteward = true })).IsError != true);
        Assert.Null((await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK))["stewardId"]);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(writing, "atlas_set_verification",
            new { spaceId = space, pageIds = new[] { page.Id }, stewardId = readerId, clearSteward = true })));
        // „Ověřit do“ a bare date: through the end of that day in Europe/Prague, the same over MCP and on the web.
        var day = DateTime.UtcNow.AddMonths(4).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var endOfDay = PageVerificationHandler.Until(DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True((await CallAsync(writing, "atlas_set_verification", new { pageId = page.Id, verify = true, until = day })).IsError != true);
        Assert.Equal(endOfDay, (await AtlasHttpSite.Json(await f.Admin.GetAsync(url), HttpStatusCode.OK))["verifiedUntil"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Equal(endOfDay, (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, url, new { verify = true, until = day }),
            HttpStatusCode.OK))["verifiedUntil"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Equal(PageVerificationStates.Verified,
            Structured(await CallAsync(reading, "atlas_get_page", new { pageId = page.Id }))["verification"]!["state"]!.GetValue<string>());
        Assert.Equal(1, Structured(await CallAsync(reading, "atlas_list_content_health", new { spaceId = space }))["summary"]!["verified"]!.GetValue<int>());
        // The reader's report reached the steward of then (the admin); over MCP it carries its note as on the web.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var listed = await CallAsync(reading, "atlas_list_notifications");
            if (Structured(listed)["items"]!.AsArray().FirstOrDefault(x => x!["kind"]!.GetValue<string>() == NotificationKinds.ReportedOutdated) is { } reported)
            {
                Assert.Equal("Staré číslo.", reported["excerpt"]!.GetValue<string>());
                Assert.Contains("„Staré číslo.“", Text(listed), StringComparison.Ordinal);
                break;
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the report was not delivered");
            await Task.Delay(50);
        }
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

    /// <summary>O3 by Prague days: a verification until a date (valid to the end of that day) is „brzy vyprší“ on the morning 14 days
    /// before and „vypršelo“ on the morning of that day, not a day later.</summary>
    [PostgreSqlFact]
    public async Task The_job_tells_the_steward_fourteen_days_before_and_on_the_day_of_expiry()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", f.SpaceId);
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Postup", Atlas.Server.Tests.Attachments.AttachmentFixture.Rich("Postup"), "Postup")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        var prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, prague)).AddDays(30);
        await f.As<PageVerificationView>(f.Writer, new SetPageVerification(page.Id, new SetPageVerificationRequest(Verify: true, Until: day.ToDateTime(TimeOnly.MinValue))));
        // The job's morning run, 06:15 in Prague.
        DateTime Morning(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(6, 15)), prague);

        foreach (var (run, kind) in new (DateOnly, string?)[] { (day.AddDays(-15), null), (day.AddDays(-14), NotificationKinds.VerificationExpiring),
            (day.AddDays(-1), NotificationKinds.VerificationExpiring), (day, NotificationKinds.VerificationExpired) })
        {
            while (await f.Host.InvokeAsync<int>(new SendVerificationNotices(Morning(run))) == SendVerificationNoticesHandler.Batch)
            {
            }
            await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);
            var notices = (await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(f.Writer,
                new Atlas.Server.Features.Notifications.Queries.ListNotifications(null))).Items.Where(x => x.SubjectId == page.Id).Select(x => x.Kind).ToList();
            Assert.Equal(kind is null ? Array.Empty<string>() : [kind], notices);
        }
    }

    /// <summary>Security L1: a later report without a note never shows an earlier reporter's note under the later reporter's name.</summary>
    [PostgreSqlFact]
    public async Task A_report_without_a_note_does_not_keep_another_reporters_note()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", f.SpaceId);
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Postup", Atlas.Server.Tests.Attachments.AttachmentFixture.Rich("Postup"), "Postup")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        async Task<Atlas.Server.Features.Notifications.Contracts.NotificationDto> ReportAsync(Atlas.Server.Platform.Identity.UserAccount reporter, string? note)
        {
            Assert.Equal(1, (await f.As<PageOutdatedReport>(reporter, new ReportPageOutdated(page.Id, new ReportPageOutdatedRequest(note)))).Notified);
            await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);
            return Assert.Single((await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(f.Writer,
                new Atlas.Server.Features.Notifications.Queries.ListNotifications(null))).Items, x => x.Kind == NotificationKinds.ReportedOutdated);
        }

        var first = await ReportAsync(f.Reader, "Staré číslo.");
        Assert.Equal(((Guid?)f.Reader.Id, "Staré číslo."), (first.ActorId, first.Excerpt));
        var second = await ReportAsync(f.Admin, null);
        Assert.Equal(((Guid?)f.Admin.Id, (string?)null, 2), (second.ActorId, second.Excerpt, second.Count));
    }

    [PostgreSqlFact]
    public async Task The_bulk_change_needs_the_space_write_right_and_selects_only_pages_the_actor_may_edit()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        await f.Site.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=@space""", ("space", Guid.Parse(space)));
        var open = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var restricted = await PageAsync(f.Admin, space, "Mzdy", Rich(Paragraph("Text.")), "Text.");
        await f.Site.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@page""", ("page", Guid.Parse(restricted.Id)));
        await f.Site.ExecuteAsync("""UPDATE "PageVerification" SET "StewardId"=NULL""");
        var (reader, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (writer, writerId) = await f.UserAsync("writer");
        await f.GrantAsync(space, writerId, "Writer");
        var (stranger, _) = await f.UserAsync("stranger");
        var bulk = $"/api/spaces/{space}/content-health/bulk";
        var all = new { action = "stewardFromLastAuthor" };

        // A page row held FOR UPDATE elsewhere: a refused caller still answers at once, so it neither selected nor locked a page, and
        // its answer names none.
        await using (var holder = new NpgsqlConnection(f.Site.ConnectionString))
        {
            await holder.OpenAsync();
            await using var hold = await holder.BeginTransactionAsync();
            await using (var locking = new NpgsqlCommand("""SELECT 1 FROM "Page" WHERE "Id"=@page FOR UPDATE""", holder, hold))
            {
                locking.Parameters.AddWithValue("page", Guid.Parse(open.Id));
                await locking.ExecuteScalarAsync();
            }
            foreach (var (client, payload, status) in new (HttpClient, object, HttpStatusCode)[]
            {
                (stranger, all, HttpStatusCode.NotFound), (stranger, new { action = "verify", pageIds = new[] { open.Id } }, HttpStatusCode.NotFound),
                (reader, all, HttpStatusCode.Forbidden),
            })
            {
                var send = AtlasHttpSite.SendAsync(client, HttpMethod.Post, bulk, payload);
                Assert.Same(send, await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(20))));
                var response = await send;
                Assert.Equal(status, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(open.Id, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(restricted.Id, body, StringComparison.OrdinalIgnoreCase);
            }
        }
        // Over MCP the same: G5 lets the reader in, the space's write right does not.
        var mcp = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"], "Write")).Secret);
        var refused = await CallAsync(mcp, "atlas_set_verification", new { spaceId = space, stewardFromLastAuthor = true });
        Assert.True(refused.IsError);
        Assert.DoesNotContain(open.Id, Text(refused), StringComparison.OrdinalIgnoreCase);

        // A writer: the restricted page is neither changed nor listed among the skipped.
        var result = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, bulk, all), HttpStatusCode.OK);
        Assert.Equal(1, result["changed"]!.GetValue<int>());
        Assert.Empty(result["skipped"]!.AsArray());
        Assert.Equal(f.AdminId, (await AtlasHttpSite.Json(await writer.GetAsync($"/api/pages/{open.Id}/verification"), HttpStatusCode.OK))["stewardId"]!.GetValue<string>());
        Assert.Null((await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{restricted.Id}/verification"), HttpStatusCode.OK))["stewardId"]);
        // A page of another space named by id is missing here, untouched.
        var other = await PageAsync(f.Admin, await f.SpaceAsync("Jiný"), "Cizí", Rich(Paragraph("Text.")), "Text.");
        var foreign = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, bulk,
            new { action = "verify", pageIds = new[] { other.Id } }), HttpStatusCode.OK);
        Assert.Equal((0, "404"), (foreign["changed"]!.GetValue<int>(), Assert.Single(foreign["skipped"]!.AsArray())!["reason"]!.GetValue<string>()));
        // „Podle posledního autora“ over chosen pages fills in a missing steward, it never replaces one.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, $"/api/pages/{open.Id}/verification", new { stewardId = writerId }), HttpStatusCode.OK);
        var kept = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, bulk,
            new { action = "stewardFromLastAuthor", pageIds = new[] { open.Id } }), HttpStatusCode.OK);
        Assert.Equal((0, "has_steward"), (kept["changed"]!.GetValue<int>(), Assert.Single(kept["skipped"]!.AsArray())!["reason"]!.GetValue<string>()));
        Assert.Equal(writerId, (await AtlasHttpSite.Json(await writer.GetAsync($"/api/pages/{open.Id}/verification"), HttpStatusCode.OK))["stewardId"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task A_reader_reporting_again_does_not_raise_the_stewards_item_again()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", f.SpaceId);
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Postup", Atlas.Server.Tests.Attachments.AttachmentFixture.Rich("Postup"), "Postup")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        async Task<Atlas.Server.Features.Notifications.Contracts.NotificationDto> ReportAsync(Atlas.Server.Platform.Identity.UserAccount reporter, string? note)
        {
            await f.As<PageOutdatedReport>(reporter, new ReportPageOutdated(page.Id, new ReportPageOutdatedRequest(note)));
            await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);
            return Assert.Single((await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(f.Writer,
                new Atlas.Server.Features.Notifications.Queries.ListNotifications(null))).Items, x => x.SubjectId == page.Id);
        }

        var first = await ReportAsync(f.Reader, "Staré číslo.");
        Assert.Equal("Staré číslo.", first.Excerpt);
        await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationsMarked>(f.Writer,
            new Atlas.Server.Features.Notifications.Commands.MarkNotificationsRead(first.Id));
        // The same reader again, at once: the read item stays read, counts nothing and keeps its note.
        var again = await ReportAsync(f.Reader, "Pořád staré.");
        Assert.Equal((true, 1, "Staré číslo."), (again.Read, again.Count, again.Excerpt));
        // Another reader raises it as before; a report without a note keeps the earlier one.
        var other = await ReportAsync(f.Admin, null);
        Assert.Equal((false, 1, f.Admin.Id, "Staré číslo."), (other.Read, other.Count, other.ActorId, other.Excerpt));
    }

    [PostgreSqlFact]
    public async Task The_SQL_interval_is_the_CSharp_interval()
    {
        short?[] values = [null, 0, 3, 6, 12];
        var cases = (from own in values from space in values from template in new[] { false, true } select (own, space, template)).ToArray();
        static string Value(short? value) => value is { } x ? $"{x}::smallint" : "NULL::smallint";
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_verification_interval_");
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        // The fragment reads its aliases p, v and s: here each is a view of one case row.
        await using var command = new NpgsqlCommand($"""
            SELECT c.n, {PageVerificationStates.IntervalSql}, {PageVerificationStates.IntervalSql}>0
            FROM (VALUES {string.Join(",", cases.Select((x, n) => $"({n},{(x.template ? "true" : "false")},{Value(x.own)},{Value(x.space)})"))})
              AS c(n, "IsTemplate", "IntervalMonths", "VerificationIntervalMonths")
            CROSS JOIN LATERAL (SELECT c."IsTemplate") p("IsTemplate")
            CROSS JOIN LATERAL (SELECT c."IntervalMonths") v("IntervalMonths")
            CROSS JOIN LATERAL (SELECT c."VerificationIntervalMonths") s("VerificationIntervalMonths")
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var checkedCases = 0;
        while (await reader.ReadAsync())
        {
            var (own, space, template) = cases[reader.GetInt32(0)];
            var expected = PageVerificationStates.Interval(own, space, template);
            Assert.Equal(expected, reader.IsDBNull(1) ? (int?)null : reader.GetInt16(1));
            Assert.Equal(expected > 0, !reader.IsDBNull(2) && reader.GetBoolean(2));
            checkedCases++;
        }
        Assert.Equal(cases.Length, checkedCases);
    }

    [PostgreSqlFact]
    public async Task A_new_steward_gets_the_reminder_the_previous_one_already_had()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", f.SpaceId);
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Postup", Atlas.Server.Tests.Attachments.AttachmentFixture.Rich("Postup"), "Postup")));
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        var now = DateTime.UtcNow;
        await f.As<PageVerificationView>(f.Writer, new SetPageVerification(page.Id, new SetPageVerificationRequest(Verify: true, Until: now.AddDays(10))));
        async Task<IReadOnlyList<string>> RunAsync(Atlas.Server.Platform.Identity.UserAccount steward)
        {
            while (await f.Host.InvokeAsync<int>(new SendVerificationNotices(now)) == SendVerificationNoticesHandler.Batch)
            {
            }
            await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);
            return (await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(steward,
                new Atlas.Server.Features.Notifications.Queries.ListNotifications(null))).Items.Where(x => x.SubjectId == page.Id).Select(x => x.Kind).ToList();
        }

        Assert.Equal(new[] { NotificationKinds.VerificationExpiring }, await RunAsync(f.Writer));
        await f.As<PageVerificationView>(f.Writer, new SetPageVerification(page.Id, new SetPageVerificationRequest(StewardId: f.Reader.Id)));
        Assert.Equal(new[] { NotificationKinds.VerificationExpiring }, await RunAsync(f.Reader));
    }

    [PostgreSqlFact]
    public async Task A_deactivated_steward_is_told_to_each_spaces_administrators_once()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        var admin = await f.Host.SeedUserAsync("spaceadmin");
        var spaces = new List<Guid>();
        foreach (var name in new[] { "První", "Druhý", "Neověřovaný" })
        {
            var space = await f.Host.SeedSpaceAsync(name, (admin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
            if (name != "Neověřovaný")
            {
                await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", space);
                spaces.Add(space);
            }
            // Two pages in a space still make one item; the space that verifies nothing makes none.
            foreach (var title in new[] { "Postup", "Návod" })
            {
                var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(space, null, title, Atlas.Server.Tests.Attachments.AttachmentFixture.Rich(title), title)));
                await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
            }
        }
        // The administrator who switched the steward's reminders off still hears of it (the notice has no preference).
        await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationPreferenceDto>(admin, new Atlas.Server.Features.Notifications.Commands.SetNotificationPreference(NotificationKinds.VerificationDue, false));
        var user = Assert.Single(await f.As<IReadOnlyList<Atlas.Server.Features.Administration.Contracts.UserSummary>>(f.Admin,
            new Atlas.Server.Features.Administration.Queries.ListUsers(Id: f.Writer.Id)));
        await f.As<Atlas.Server.Features.Administration.Contracts.UserSummary>(f.Admin,
            new Atlas.Server.Features.Administration.Commands.SaveUser(f.Writer.Id, new(user.DisplayName, false, false, user.Version)));
        await Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);

        async Task<List<Atlas.Server.Features.Notifications.Contracts.NotificationDto>> InboxAsync(Atlas.Server.Platform.Identity.UserAccount account)
            => (await f.As<Atlas.Server.Features.Notifications.Contracts.NotificationList>(account, new Atlas.Server.Features.Notifications.Queries.ListNotifications(null)))
                .Items.Where(x => x.Kind == NotificationKinds.StewardDeactivated).ToList();
        var items = await InboxAsync(admin);
        Assert.Equal(spaces.Order(), items.Select(x => x.SubjectId).Order());
        Assert.All(items, x => Assert.Equal(("atlas.space", f.Writer.Id, 1), (x.SubjectType, x.ActorId, x.Count)));
        Assert.Empty(await InboxAsync(f.Reader));
    }

    /// <summary>The panel's „Čtenost“ and Stav obsahu count the same calendar days in Prague: a view just before the first day of the
    /// period counts in neither (a rolling 90 days would count it in the panel), one at its first moment in both. The space's readership
    /// over MCP gives the same counts, the last view as a day only and never a name, its wiki text inside the content boundary; a reader of the space may not ask for it.</summary>
    [PostgreSqlFact]
    public async Task The_panel_Stav_obsahu_and_the_content_health_tool_count_the_same_days()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        await f.Site.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=@space""", ("space", Guid.Parse(space)));
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var (reader, readerId) = await f.UserAsync("vzacna");
        await f.GrantAsync(space, readerId, "Reader");
        var prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, prague));
        var first = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(1 - PageVerificationReader.ReaderDays).ToDateTime(TimeOnly.MinValue), prague);
        await f.Site.ExecuteAsync("""
            INSERT INTO "PageView"("Id","PageId","UserId","ViewedAt") VALUES (gen_random_uuid(),@page,@reader,@first),(gen_random_uuid(),@page,@admin,@before)
            """, ("page", Guid.Parse(page.Id)), ("reader", Guid.Parse(readerId)), ("admin", Guid.Parse(f.AdminId)), ("first", first), ("before", first.AddMinutes(-1)));

        var bundle = (await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{page.Id}?bundle=true"), HttpStatusCode.OK))["bundle"]!;
        var health = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/content-health?days={PageVerificationReader.ReaderDays}"), HttpStatusCode.OK);
        Assert.Equal((1L, 1L), (bundle["readers"]!.GetValue<long>(), Assert.Single(health["items"]!.AsArray())!["readers"]!.GetValue<long>()));

        var agent = await f.ClientAsync(await f.AdminTokenAsync("read"));
        var args = new { spaceId = space, days = PageVerificationReader.ReaderDays };
        var read = await CallAsync(agent, "atlas_list_content_health", args);
        var item = Assert.Single(Structured(read)["items"]!.AsArray())!;
        Assert.Equal((page.Id, 1L, 1L, today.AddDays(1 - PageVerificationReader.ReaderDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            (item["pageId"]!.GetValue<string>(), item["readers"]!.GetValue<long>(), item["views"]!.GetValue<long>(), item["lastViewedAt"]!.GetValue<string>()));
        Assert.DoesNotContain("vzacna", Text(read), StringComparison.Ordinal);
        Assert.Contains("<wiki-content>", Text(read), StringComparison.Ordinal);
        var asReader = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"], "Read")).Secret);
        Assert.Equal(403, Structured(await CallAsync(asReader, "atlas_list_content_health", args))["status"]!.GetValue<int>());
    }
}
