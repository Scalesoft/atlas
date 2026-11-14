namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// Files that by name look like certificates, private keys or stored credentials. The import still
/// copies one a page links to, but the report points each of them out so it is not published unknowingly.
/// </summary>
public static class SensitiveFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pfx", ".p12", ".pem", ".key", ".jks", ".keystore", ".kdbx", ".ppk", ".ovpn",
    };

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".env", ".htpasswd", ".pgpass", ".netrc",
    };

    public static bool IsSensitive(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return Names.Contains(name) || Extensions.Contains(Path.GetExtension(name));
    }
}
