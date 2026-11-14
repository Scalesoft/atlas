using Atlas.Server.Features.Auth.OpenIdConnect;
namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>The OIDC settings the running process was built with. A stored version different from this one means a restart is pending.</summary>
public sealed record OidcRuntimeConfiguration(Guid? Version, AtlasOidcOptions Options)
{
    /// <summary>The configured public origin ("https://wiki.example.com"), the address links that leave a request use (MCP, PDF);
    /// null when none is configured.</summary>
    public string? PublicOrigin => Options.PublicOrigin is { Length: > 0 } origin && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        ? uri.GetLeftPart(UriPartial.Authority)
        : null;
}
