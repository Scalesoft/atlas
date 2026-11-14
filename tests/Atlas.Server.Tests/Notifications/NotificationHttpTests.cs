using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Notifications;

/// <summary>
/// The rights matrix of the inbox (inbox spec §5, §8; its rules are NotificationTests'): every web route, /api/v1 route and MCP tool for
/// the recipient, an account the subject is closed to, a read-only and a draft token and an anonymous caller. Only the caller's own items
/// exist for it; a closed subject leaves no trace; read-only tokens read but do not mark; the preferences are the web's only.
/// </summary>
public sealed class NotificationHttpTests
{
    [PostgreSqlFact]
    public async Task Every_route_and_tool_answers_each_actor_as_its_rights_say_and_nothing_closed_leaks()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.SpaceAsync("Tým");
        var (reader, readerId) = await f.UserAsync("reader", "write");
        var (outsider, outsiderId) = await f.UserAsync("outsider", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (page, _) = await PageAsync(f.Admin, space, "Plán", Rich(Paragraph("Text")), "Text");
        Assert.Equal(HttpStatusCode.Created, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{page}/comments",
            new { body = $"[reader](a-contact:?internalUser.id={readerId}) a [outsider](a-contact:?internalUser.id={outsiderId})" })).StatusCode);

        // Delivered by the site's own outbox dispatcher after the commit.
        var watch = Stopwatch.StartNew();
        while ((await AtlasHttpSite.Json(await reader.GetAsync("/api/notifications/unread"), HttpStatusCode.OK))["count"]!.GetValue<int>() == 0)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the notification was not delivered");
            await Task.Delay(50);
        }
        var listed = await AtlasHttpSite.Json(await reader.GetAsync("/api/notifications"), HttpStatusCode.OK);
        var item = listed["items"]![0]!;
        var id = item["id"]!.GetValue<string>();
        Assert.Equal(("comment.mention", page, "Plán", 1), (item["kind"]!.GetValue<string>(), item["subjectId"]!.GetValue<string>(), item["subjectTitle"]!.GetValue<string>(),
            listed["unread"]!.GetValue<int>()));

        // The outsider's own item exists, but its subject is closed: no item, no count, and another's item is not reachable.
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "Notification" WHERE "RecipientId"=@id""", ("id", Guid.Parse(outsiderId))));
        var closed = await AtlasHttpSite.Json(await outsider.GetAsync("/api/notifications"), HttpStatusCode.OK);
        Assert.Equal((0, 0), (closed["items"]!.AsArray().Count, closed["unread"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(outsider, HttpMethod.Post, $"/api/notifications/{id}/read")).StatusCode);

        using var anonymous = f.Site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications")).StatusCode);

        using var readToken = Bearer(f, (await IssueAsync(reader, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);
        var drafter = await IssueAsync(reader, f.Site.Password, ["read", "write"], ["rest"], "Drafter");
        using var draftToken = Bearer(f, drafter.Secret);
        Assert.Equal(id, (await AtlasHttpSite.Json(await readToken.GetAsync("/api/v1/notifications"), HttpStatusCode.OK))["items"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, (await AtlasHttpSite.Json(await readToken.GetAsync("/api/v1/notifications/unread"), HttpStatusCode.OK))["count"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Forbidden, (await readToken.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readToken.PostAsync("/api/v1/notifications/read-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await readToken.GetAsync("/api/v1/notifications/preferences")).StatusCode);
        var marked = await AtlasHttpSite.Json(await draftToken.PostAsync($"/api/v1/notifications/{id}/read", null), HttpStatusCode.OK);
        Assert.Equal((1, 0), (marked["marked"]!.GetValue<int>(), marked["unread"]!.GetValue<int>()));
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "ApiActivity" WHERE "Operation"='notifications.read' AND "Outcome"='ok' AND "TokenId"=@token""",
            ("token", Guid.Parse(drafter.Id))));

        // MCP as the token's owner: a read token lists and cannot mark; a draft token marks all.
        var readMcp = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"], "Reader MCP")).Secret);
        var outsiderMcp = await f.ClientAsync((await IssueAsync(outsider, f.Site.Password, ["read"], ["mcp"], "Outsider MCP")).Secret);
        Assert.Equal(id, Items(await CallAsync(readMcp, "atlas_list_notifications"))[0]!["notificationId"]!.GetValue<string>());
        Assert.Empty(Items(await CallAsync(outsiderMcp, "atlas_list_notifications")));
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(readMcp, "atlas_mark_notifications_read")));
        var draftMcp = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"], "Drafter MCP")).Secret);
        Assert.Equal(0, Structured(await CallAsync(draftMcp, "atlas_mark_notifications_read"))["unread"]!.GetValue<int>());
        // The space switched out of AI hides its items on MCP only.
        await f.ExcludeFromAiAsync(space);
        Assert.Empty(Items(await CallAsync(readMcp, "atlas_list_notifications")));
        Assert.Single((await AtlasHttpSite.Json(await reader.GetAsync("/api/notifications"), HttpStatusCode.OK))["items"]!.AsArray());

        // The preferences: the web's own, for the caller only.
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Put, "/api/notifications/preferences/page.mention", new { enabled = false })).StatusCode);
        var preferences = await AtlasHttpSite.Json(await reader.GetAsync("/api/notifications/preferences"), HttpStatusCode.OK);
        Assert.False(preferences["items"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "page.mention")!["enabled"]!.GetValue<bool>());
        Assert.True((await AtlasHttpSite.Json(await outsider.GetAsync("/api/notifications/preferences"), HttpStatusCode.OK))["items"]!.AsArray()
            .All(x => x!["enabled"]!.GetValue<bool>()));
    }

    private static HttpClient Bearer(McpSite f, string secret)
    {
        var client = f.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
