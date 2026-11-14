using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings;

/// <summary>Deliberately static, against spec §7.7: the OIDC registration calls Load before WebApplication.Build(),
/// when no container exists yet.</summary>
public static class OidcCertificates
{
    private const int MaxLockAttempts = 100;

    public static OidcCertificateSet Load(IConfiguration configuration, IHostEnvironment environment, AtlasOidcOptions options, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(options.SigningCertificatePath) || !string.IsNullOrEmpty(options.EncryptionCertificatePath))
        {
            if (string.IsNullOrEmpty(options.SigningCertificatePath) || string.IsNullOrEmpty(options.EncryptionCertificatePath))
            {
                throw new InvalidOperationException("Configure both OIDC signing and encryption PFX paths, or neither for automatic keys.");
            }
            var signing = LoadExplicit(options.SigningCertificatePath, options.CertificatePassword, now);
            try
            {
                return new OidcCertificateSet([signing], [LoadExplicit(options.EncryptionCertificatePath, options.CertificatePassword, now)]);
            }
            catch
            {
                signing.Dispose();
                throw;
            }
        }
        var directory = Path.GetFullPath(configuration["Authentication:OpenIdConnect:CertificateDirectory"] ?? Path.Combine(environment.ContentRootPath, ".data", "oidc-certificates"));
        PrivateServerFiles.PrepareDirectory(directory, environment);
        using var lease = AcquireLock(Path.Combine(directory, ".certificate.lock"));
        var signingKeys = LoadOrCreate(directory, "signing", X509KeyUsageFlags.DigitalSignature, now);
        try
        {
            return new OidcCertificateSet(signingKeys, LoadOrCreate(directory, "encryption", X509KeyUsageFlags.KeyEncipherment, now));
        }
        catch
        {
            foreach (var key in signingKeys)
            {
                key.Dispose();
            }
            throw;
        }
    }

    private static X509Certificate2 LoadExplicit(string path, string? password, DateTimeOffset now)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        if (certificate.HasPrivateKey && certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && certificate.NotAfter.ToUniversalTime() > now.UtcDateTime)
        {
            return certificate;
        }
        certificate.Dispose();
        throw new InvalidOperationException("OIDC certificate requires a private key and current validity.");
    }

    private static List<X509Certificate2> LoadOrCreate(string directory, string purpose, X509KeyUsageFlags usage, DateTimeOffset now)
    {
        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, purpose + "-*.pfx"))
            {
                PrivateServerFiles.ProtectFile(path);
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey)
                {
                    certificate.Dispose();
                    throw new InvalidOperationException("Stored OIDC certificate has no private key.");
                }
                if (certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime)
                {
                    certificate.Dispose();
                }
                else
                {
                    certificates.Add(certificate);
                }
            }
            if (certificates.Any(c => c.NotBefore.ToUniversalTime() <= now.UtcDateTime && c.NotAfter.ToUniversalTime() > now.AddDays(30).UtcDateTime))
            {
                return certificates;
            }
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(new X500DistinguishedName("CN=Atlas internal OIDC " + purpose), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
            using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));
            var bytes = generated.Export(X509ContentType.Pkcs12);
            var temporary = Path.Combine(directory, ".new-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var file = new FileStream(temporary, PrivateServerFiles.CreateOptions(FileMode.CreateNew)))
                {
                    file.Write(bytes);
                    file.Flush(true);
                }
                PrivateServerFiles.ProtectFile(temporary);
                var final = Path.Combine(directory, purpose + "-" + generated.Thumbprint + ".pfx");
                File.Move(temporary, final);
                certificates.Add(X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            return certificates;
        }
        catch
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
            throw;
        }
    }

    private static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, PrivateServerFiles.CreateOptions(FileMode.OpenOrCreate));
                try
                {
                    PrivateServerFiles.ProtectFile(path);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (attempt < MaxLockAttempts)
            {
                Thread.Sleep(50);
            }
        }
    }
}
