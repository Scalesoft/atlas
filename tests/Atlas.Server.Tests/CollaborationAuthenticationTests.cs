using Microsoft.AspNetCore.Http;
using Atlas.Markdown.Sync;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class CollaborationAuthenticationTests
{
    [Fact]
    public void Presence_identity_does_not_carry_session_proof_and_cannot_impersonate_another_session()
    {
        var sessions = new CollaborationSessions();
        var actor = new UserAccount { Id = Guid.NewGuid(), DisplayName = "Writer", SecurityStamp = "first" };
        var connected = sessions.Register(actor);
        Assert.Equal(actor.Id.ToString(), connected.Id);
        Assert.True(sessions.Matches(connected, actor));
        Assert.False(sessions.Matches(new CollabUser(connected.Id, connected.Name), actor));
        actor.SecurityStamp = "rotated";
        Assert.False(sessions.Matches(connected, actor));
        Assert.True(sessions.Matches(sessions.Register(actor), actor));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("https://localhost:7101", "same-origin", true)]
    [InlineData("https://evil.example", null, false)]
    [InlineData("https://localhost:7101", "cross-site", false)]
    [InlineData("http://localhost:7101", null, false)]
    [InlineData("null", null, false)]
    [InlineData("https://localhost:7101/path", null, false)]
    public void Cookie_hub_rejects_cross_origin_handshakes(string? origin, string? site, bool allowed)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https"; request.Host = new HostString("localhost", 7101);
        if (origin is not null)
        {
            request.Headers.Origin = origin;
        }
        if (site is not null)
        {
            request.Headers["Sec-Fetch-Site"] = site;
        }
        Assert.Equal(allowed, CollaborationOrigin.IsAllowed(request));
    }
}
