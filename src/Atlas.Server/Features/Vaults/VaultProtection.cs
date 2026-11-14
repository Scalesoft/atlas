using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// Data Protection for the two vault passwords Atlas stores: the SMB password of a location and the KDBX master
/// password of a vault (automatic unlock after a restart). Same pattern as the backup destination password; the
/// key ring lives in <c>DataProtection:KeysPath</c>. A payload that no longer decrypts (lost key ring) yields null,
/// and the administrator enters the password again.
/// </summary>
public sealed class VaultProtection
{
    public const string LocationPasswordPurpose = "ScaleAtlas.VaultLocation.Password.v1";
    public const string KdbxPasswordPurpose = "ScaleAtlas.Vault.KdbxPassword.v1";

    private readonly IDataProtector m_locationPasswords;
    private readonly IDataProtector m_kdbxPasswords;

    public VaultProtection(IDataProtectionProvider provider)
    {
        m_locationPasswords = provider.CreateProtector(LocationPasswordPurpose);
        m_kdbxPasswords = provider.CreateProtector(KdbxPasswordPurpose);
    }

    public string ProtectLocationPassword(string password) => m_locationPasswords.Protect(password);

    public string? UnprotectLocationPassword(string? payload) => Unprotect(m_locationPasswords, payload);

    public string ProtectKdbxPassword(string password) => m_kdbxPasswords.Protect(password);

    public string? UnprotectKdbxPassword(string? payload) => Unprotect(m_kdbxPasswords, payload);

    private static string? Unprotect(IDataProtector protector, string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }
        try
        {
            return protector.Unprotect(payload);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
