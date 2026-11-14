using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.OpenIdConnect.Commands;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class IdentityLinkRedactionTests
{
    [Fact]
    public void A_link_authorization_never_formats_the_password_for_logs()
    {
        var command = new AuthorizeIdentityLink("sensitive-test-password", false);
        Assert.DoesNotContain(command.Password!, command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_request_body_never_formats_the_password_for_logs()
    {
        var request = new LinkExternalIdentity("sensitive-test-password", "/page/test");
        Assert.DoesNotContain(request.Password!, request.ToString(), StringComparison.Ordinal);
    }
}
