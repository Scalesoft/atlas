using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

public sealed class AtlasOidcOptions
{
    public bool BuiltinEnabled { get; set; } = true;

    public string PublicOrigin { get; set; } = "";

    public string? SigningCertificatePath { get; set; }

    public string? EncryptionCertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public AtlasOidcProvider[] Providers { get; set; } = [];

    public static AtlasOidcOptions Read(IConfiguration configuration, bool validate = true)
    {
        var options = new AtlasOidcOptions { BuiltinEnabled = configuration.GetValue("Authentication:Builtin:Enabled", true) };
        configuration.GetSection("Authentication:OpenIdConnect").Bind(options);
        options.BuiltinEnabled = configuration.GetValue("Authentication:Builtin:Enabled", true);
        if (validate)
        {
            options.Validate();
        }
        return options;
    }

    public void Validate()
    {
        if (!BuiltinEnabled && Providers.Length == 0)
        {
            throw new InvalidOperationException("At least one authentication module must be enabled.");
        }
        if (Providers.Length == 0)
        {
            return;
        }
        if (!IsHttps(PublicOrigin, true))
        {
            throw new InvalidOperationException("Authentication:OpenIdConnect:PublicOrigin must be an absolute HTTPS origin without path, credentials, query or fragment.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issuers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in Providers)
        {
            OidcAdminMapping.Validate(provider.AdminMappings ?? throw new InvalidOperationException("Administrator mappings cannot be null."));
            if (!Regex.IsMatch(provider.Id, "^[a-z][a-z0-9-]{0,39}$") || !ids.Add(provider.Id))
            {
                throw new InvalidOperationException("OIDC provider IDs must be unique lowercase URL-safe identifiers.");
            }
            if (!IsHttps(provider.Issuer, false) || !issuers.Add(provider.Issuer))
            {
                throw new InvalidOperationException("OIDC issuers must be unique absolute HTTPS URLs without credentials, query or fragment.");
            }
            if (string.IsNullOrWhiteSpace(provider.ClientId) || string.IsNullOrWhiteSpace(provider.DisplayName))
            {
                throw new InvalidOperationException("OIDC provider requires ClientId and DisplayName.");
            }
            if (provider.EnableProviderLogout)
            {
                throw new InvalidOperationException("Provider logout is not enabled in this release; local logout remains available.");
            }
            if (provider.Scopes.Any(s => s == "offline_access" || string.IsNullOrWhiteSpace(s) || s.Any(char.IsWhiteSpace)))
            {
                throw new InvalidOperationException("OIDC scopes cannot request offline access and must be individual names.");
            }
        }
    }

    private static bool IsHttps(string value, bool originOnly)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && (!originOnly || uri.AbsolutePath == "/");
    }
}
