namespace Atlas.Server.Features.Auth.OpenIdConnect.Commands;

/// <summary>
/// In-process only: constructed at the one link endpoint and invoked in the same request, never
/// published or persisted. It still redacts, because a failed invocation is reported with ToString().
/// </summary>
public sealed record AuthorizeIdentityLink(string? Password, bool RecentProviderAuthentication)
{
    public override string ToString() => "AuthorizeIdentityLink { redacted }";
}
