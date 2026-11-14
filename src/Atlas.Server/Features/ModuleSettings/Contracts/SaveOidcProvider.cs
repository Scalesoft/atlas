namespace Atlas.Server.Features.ModuleSettings.Contracts;

public sealed record SaveOidcProvider(string Id, string DisplayName, string Issuer, string ClientId, string? ClientSecret,
    bool ClearSecret, string[] Scopes, bool AllowSignup, Auth.OpenIdConnect.OidcAdminMapping[]? AdminMappings = null)
{
    public override string ToString() => "SaveOidcProvider { sensitive values redacted }";
}
