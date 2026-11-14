using Atlas.Server.Features.Auth.OpenIdConnect;
namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>One settings revision read back with its providers and their plaintext secrets. Never leaves the server.</summary>
internal sealed record StoredOidcSettings(Guid? Version, string PublicOrigin, AtlasOidcProvider[] Providers);
