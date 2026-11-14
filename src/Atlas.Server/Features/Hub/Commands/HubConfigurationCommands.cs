using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.Hub.Commands;

public sealed record PrepareHubSettings(SaveHubSettings Request);
public sealed record PreparedHubSettings(Guid ActorId, string SecurityStamp, string? ExpectedVersion, string Issuer, string BaseUrl, HubConnectionMode Mode, string ProtectedToken)
{
    public override string ToString() => $"PreparedHubSettings {{ ActorId = {ActorId}, Mode = {Mode}, Credentials = [REDACTED] }}";
}
public sealed record CommitHubSettings(PreparedHubSettings Settings);

