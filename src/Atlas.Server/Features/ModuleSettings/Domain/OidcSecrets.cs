using Microsoft.AspNetCore.DataProtection;

namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>Decides what a save does to a stored client secret: replace it, clear it, or keep the protected payload as is.</summary>
public static class OidcSecrets
{
    public static string? Resolve(string? replacement, bool clear, string? existingProtected, IDataProtector protector)
    {
        if (clear && !string.IsNullOrEmpty(replacement))
        {
            throw new ApiException(400, "Vyberte zadání nového secretu nebo jeho odebrání.");
        }
        return clear ? null : string.IsNullOrEmpty(replacement) ? existingProtected : protector.Protect(replacement);
    }
}
