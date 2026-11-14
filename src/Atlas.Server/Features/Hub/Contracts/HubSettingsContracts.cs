namespace Atlas.Server.Features.Hub.Contracts;

public sealed record SaveHubSettings(string Issuer, string BaseUrl, string Mode, string? Token, string? ExpectedVersion)
{
    public override string ToString() => $"SaveHubSettings {{ Issuer = {Issuer}, Mode = {Mode}, Token = [REDACTED] }}";
}
public sealed record AttachHubGroup(string ExternalGroupId, string? ExpectedVersion);
public sealed record UpdateHubBinding(bool Enabled, bool GrantsWikiAdmin, string ExpectedVersion);
public sealed record VerifyHubIdentities(Guid? UserId = null);
public sealed record HubProfileIdentity(string State, DateTime? VerifiedUntil, DateTime? LastSuccessAt, DateTime? LastAttemptAt, string? LastErrorCode);
public sealed record HubProfileView(string? Mode, HubProfileIdentity? Identity);
public sealed record HubProviderView(string Issuer, string DisplayName);
public sealed record HubHealthView(Guid ConnectionId, string State, int Known, int Pending, int Expired, int Errors, DateTime? LastSuccessAt);
public sealed record HubBindingView(Guid GroupId, string Name, string ExternalGroupId, bool Enabled, bool GrantsWikiAdmin, string Version, string HubUrl);
public sealed record HubIdentityView(Guid UserId, string DisplayName, Guid ExternalIdentityId, string State, DateTime? VerifiedUntil, DateTime? LastSuccessAt, DateTime? LastAttemptAt, string? LastErrorCode);
public sealed record HubSettingsView(Guid? ConnectionId, string? Version, string Issuer, string BaseUrl, bool HasToken, string Mode,
    IReadOnlyList<HubProviderView> Providers, IReadOnlyList<HubBindingView> Bindings, IReadOnlyList<HubIdentityView> Identities,
    int PollSeconds = 60, int MaxValiditySeconds = 300, HubHealthView? Health = null);
