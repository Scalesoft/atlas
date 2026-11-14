using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class GrantValidityHttpTests
{
    [PostgreSqlFact]
    public async Task HTTP_roundtrips_UTC_bounds_filters_space_access_and_preserves_legacy_unbounded_requests()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_grant_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var login = TestLogin.For("bounded");
        var invitationResponse = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName = "Bounded reader", isWikiAdmin = false });
        Assert.Equal(HttpStatusCode.Created, invitationResponse.StatusCode);
        var invitation = (await invitationResponse.Content.ReadFromJsonAsync<CreatedBuiltinUser>())!;
        using var member = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(member, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(member, HttpMethod.Post, "/api/auth/login",
            new { username = login, password = site.Password })).StatusCode);
        var path = $"/api/spaces/{spaceId}/access";
        string? version = null;
        foreach (var (start, end, visible) in new[]
        {
            ("2090-01-01T02:00:00+02:00", "2090-01-02T02:00:00+02:00", false),
            ("2000-01-01T00:00:00Z", "2090-01-01T00:00:00Z", true)
        })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path,
                new { userId = invitation.User.Id, role = "Reader", expectedVersion = version, startAt = start, expiresAt = end })).StatusCode);
            var access = (await admin.GetFromJsonAsync<SpaceAccessSummary>(path))!;
            var grant = Assert.Single(access.Grants, x => x.UserId == invitation.User.Id);
            Assert.Equal(DateTimeOffset.Parse(start).UtcDateTime, grant.StartAt);
            Assert.Equal(DateTimeKind.Utc, grant.StartAt!.Value.Kind);
            Assert.Equal(DateTimeOffset.Parse(end).UtcDateTime, grant.ExpiresAt);
            version = grant.Version;
            var available = (await member.GetFromJsonAsync<JsonArray>("/api/spaces"))!;
            Assert.Equal(visible ? 1 : 0, available.Count);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path,
            new { userId = invitation.User.Id, role = "Reader", expectedVersion = version,
                startAt = "2090-01-02T00:00:00Z", expiresAt = "2090-01-01T00:00:00Z" })).StatusCode);
        // A validity that has already ended is refused with the reason, not saved as a grant that never applies.
        var lapsed = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path,
            new { userId = invitation.User.Id, role = "Reader", expectedVersion = version, startAt = "2000-01-01T00:00:00Z", expiresAt = "2001-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, lapsed.StatusCode);
        Assert.Equal("Konec platnosti už uplynul. Zvolte datum a čas v budoucnosti.", (await lapsed.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(member, HttpMethod.Put, path,
            new { userId = invitation.User.Id, role = "Admin", expectedVersion = version })).StatusCode);
        // Existing clients omit both new fields; this remains the explicit unbounded form.
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path,
            new { userId = invitation.User.Id, role = "Reader", expectedVersion = version })).StatusCode);
        var final = Assert.Single((await admin.GetFromJsonAsync<SpaceAccessSummary>(path))!.Grants, x => x.UserId == invitation.User.Id);
        Assert.Null(final.StartAt);
        Assert.Null(final.ExpiresAt);
    }
}
