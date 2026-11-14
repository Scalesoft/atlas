namespace Atlas.Server.Features.Auth.OpenIdConnect;

public sealed class AtlasOidcProvider
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Issuer { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string? ClientSecret { get; set; }

    public string[] Scopes { get; set; } = ["openid", "profile"];

    public bool AllowSignup { get; set; }

    public OidcAdminMapping[] AdminMappings { get; set; } = [];

    public bool EnableProviderLogout { get; set; }
}
