using Atlas.Server.Features.Security.Networks;

namespace Atlas.Server.Features.Security;

/// <summary>Where a request comes from, as raw evidence; <see cref="ReleasePolicy"/> resolves it against the current configuration
/// version on every evaluation, so no network status is ever kept in a session or token.</summary>
public sealed record ClientContext(ClientAddressEvidence Evidence)
{
    public static ClientContext From(HttpContext? context) => new(ClientAddressEvidence.Of(context));
}
