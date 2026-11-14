namespace Atlas.Server.Features.ModuleSettings.Contracts;

/// <param name="LinkedAccounts">The accounts linked to an identity of the provider's issuer: who loses this way in when it is removed.</param>
public sealed record OidcProviderView(string Id, string DisplayName, string Issuer, string ClientId, bool HasSecret,
    string[] Scopes, bool AllowSignup, string CallbackUrl, Auth.OpenIdConnect.OidcAdminMapping[]? AdminMappings = null, int LinkedAccounts = 0);
