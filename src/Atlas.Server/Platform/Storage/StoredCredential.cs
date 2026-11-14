namespace Atlas.Server.Platform.Storage;

/// <summary>
/// A saved storage password goes only to the SMB server and account it was saved for (SEC-T-2): otherwise an administrator's session
/// could point a test or a save at a server of its choice, which receives an NTLM response to crack the password offline. A changed
/// host, domain or account has to type the password again; a local folder sends no password at all. One rule for backups, storage
/// profiles and vault locations.
/// </summary>
public static class StoredCredential
{
    public const string RetypeMessage = "Při změně serveru nebo přihlašovacího účtu zadejte heslo znovu.";

    public static bool Reusable(string savedLocation, string savedUsername, string savedDomain, string location, string username, string domain)
        => Host(location) is not { } host
            || string.Equals(Host(savedLocation), host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(savedUsername, username, StringComparison.Ordinal)
            && string.Equals(savedDomain, domain, StringComparison.Ordinal);

    /// <summary>Refuses the reuse with 400 when <see cref="Reusable"/> says no.</summary>
    public static void Require(string savedLocation, string savedUsername, string savedDomain, string location, string username, string domain)
    {
        if (!Reusable(savedLocation, savedUsername, savedDomain, location, username, domain))
        {
            throw new ApiException(400, RetypeMessage);
        }
    }

    private static string? Host(string location)
        => location.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri) ? uri.IdnHost : null;
}
