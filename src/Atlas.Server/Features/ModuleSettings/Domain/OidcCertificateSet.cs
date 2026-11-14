using System.Security.Cryptography.X509Certificates;

namespace Atlas.Server.Features.ModuleSettings.Domain;

public sealed record OidcCertificateSet(IReadOnlyList<X509Certificate2> Signing, IReadOnlyList<X509Certificate2> Encryption) : IDisposable
{
    public void Dispose()
    {
        foreach (var certificate in Signing.Concat(Encryption))
        {
            certificate.Dispose();
        }
    }
}
