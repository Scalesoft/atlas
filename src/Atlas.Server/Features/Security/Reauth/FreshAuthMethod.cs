namespace Atlas.Server.Features.Security.Reauth;

/// <summary>Whether a sign-in method can prove fresh authentication: the local password always can; an OIDC provider only after a
/// validated fresh auth_time has been seen (self-test or a real re-authentication), Unsupported after a missing or stale one,
/// Unknown before either.</summary>
public enum FreshAuthSupport
{
    Supported,
    Unknown,
    Unsupported
}

/// <summary>A way the account can re-authenticate now: password (the builtin credential) or oidc with ProviderId.</summary>
public sealed record FreshAuthMethod(string Method, string? ProviderId, string? ProviderName, FreshAuthSupport Support)
{
    public static string Name(FreshAuthSupport support) => support switch
    {
        FreshAuthSupport.Supported => "supported",
        FreshAuthSupport.Unsupported => "unsupported",
        _ => "unknown"
    };
}
