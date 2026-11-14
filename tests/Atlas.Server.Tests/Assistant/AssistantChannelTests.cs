using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The content channel assistant (spec §4.3): only the marker the assistant's tool session puts into HttpContext.Items makes a request
/// assistant; nothing the client sends does. A personal API identity with the marker fails closed, and the assistant's audited writes
/// carry no token.
/// </summary>
public sealed class AssistantChannelTests
{
    [Fact]
    public void The_marker_makes_a_browser_request_assistant_and_nothing_else_does()
    {
        var context = Browser();
        context.Request.Headers["X-Atlas-Channel"] = "assistant";
        Assert.Equal(RequestChannel.Web, Channel(context).Current);

        context.Items[HttpRequestChannel.AssistantMarker] = true;
        var current = Channel(context).Current;
        Assert.Equal(ContentChannel.Assistant, current.Channel);
        Assert.Null(current.ApiTokenId);
        Assert.Equal("assistant", current.Code);
        Assert.True(current.RefusesOwnUnsaved);
        Assert.False(current.IsMcp);
        Assert.False(RequestChannel.Web.RefusesOwnUnsaved);
    }

    [Fact]
    public void A_personal_api_identity_with_the_marker_fails_closed()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("api_token_id", Guid.NewGuid().ToString())], HttpRequestChannel.PersonalApiScheme))
        };
        context.Request.Path = "/mcp";
        Assert.Equal(ContentChannel.Mcp, Channel(context).Current.Channel);

        context.Items[HttpRequestChannel.AssistantMarker] = true;
        Assert.Equal(401, Assert.Throws<ApiException>(() => Channel(context).Current).Status);
        Assert.Throws<ArgumentException>(() => new RequestChannel(ContentChannel.Assistant, Guid.NewGuid()));
    }

    [PostgreSqlFact]
    public async Task An_assistant_write_is_audited_as_its_user_without_a_token()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_assistant_channel_");
        var user = await host.SeedUserAsync("jana");
        var pageId = Guid.NewGuid();
        var context = Browser(user.Id);
        context.Items[HttpRequestChannel.AssistantMarker] = true;
        using (var scope = host.Services.CreateScope())
        {
            var accessor = new HttpContextAccessor { HttpContext = context };
            var audit = new ApiActivityAudit(accessor, new HttpRequestChannel(accessor), scope.ServiceProvider.GetRequiredService<IMessageBus>(),
                NullLogger<ApiActivityAudit>.Instance);
            await audit.RecordAsync("atlas_set_tags", pageId, null, "ok", null);
            await audit.RecordAsync("atlas_publish", pageId, null, "collaboration_unsaved", null);
        }

        using var session = host.OpenSession();
        var rows = await session.CreateSQLQuery("""
            SELECT e."Action" || ':' || a."Channel" || ':' || coalesce(CAST(a."TokenId" AS text),'-') || ':' || a."Outcome"
            FROM "ApiActivity" a JOIN "AccessAuditEvent" e ON e."ApiActivityId"=a."Id" WHERE a."ActorId"=:actor ORDER BY a."Id"
            """).SetParameter("actor", user.Id).ListAsync<string>();
        Assert.Equal(["assistant.write:assistant:-:ok", "assistant.publish:assistant:-:collaboration_unsaved"], rows);
    }

    private static HttpRequestChannel Channel(HttpContext context) => new(new HttpContextAccessor { HttpContext = context });

    private static DefaultHttpContext Browser(Guid? userId = null) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString())], "Cookies"))
    };
}
