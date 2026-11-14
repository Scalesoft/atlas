namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>The Data Protection purpose for stored client secrets. Changing this string makes every stored secret undecryptable.</summary>
internal static class OidcProtection
{
    public const string Purpose = "ScaleAtlas.Modules.Oidc.ClientSecret.v1";
}
