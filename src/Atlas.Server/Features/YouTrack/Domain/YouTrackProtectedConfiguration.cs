using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Domain;

/// <summary>
/// One read of the effective integration configuration as a message carries it out of a transaction (ReadYouTrackConfiguration):
/// the token stays data-protected here (<c>SaveYouTrackSettingsHandler.TokenPurpose</c>, "" = none), the way
/// <c>AiProfileSnapshot</c> keeps its API key, and <see cref="Unprotect"/> turns it into the plain
/// <see cref="YouTrackConfigurationSnapshot"/> only where the options are used. <see cref="ToString"/> never prints the token.
/// </summary>
public sealed record YouTrackProtectedConfiguration(string Revision, bool Enabled, string BaseUrl, string ProtectedToken,
    Dictionary<Guid, string[]> SpaceProjects)
{
    /// <summary>The plain options. A token the key ring can no longer unprotect makes the integration unavailable.</summary>
    public YouTrackConfigurationSnapshot Unprotect(IDataProtector protector)
    {
        string token;
        try
        {
            token = ProtectedToken.Length == 0 ? "" : protector.Unprotect(ProtectedToken);
        }
        catch (CryptographicException)
        {
            throw new YouTrackUnavailableException();
        }
        return new YouTrackConfigurationSnapshot(Revision, new YouTrackOptions
        {
            Enabled = Enabled, BaseUrl = BaseUrl, Token = token, SpaceProjects = SpaceProjects
        });
    }

    /// <summary>Identifies the configuration without any secret.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Revision = ").Append(Revision).Append(", Enabled = ").Append(Enabled);
        return true;
    }
}
