using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GlamByMariya.Web.Security;

// Loads the production Data Protection certificate (protects login cookies and keys).
internal static class DataProtectionCertificate
{
    internal static X509Certificate2 Load(IConfiguration configuration)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(configuration["DataProtection:CertificatePfxBase64"] ?? ""),
                configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7) || rsa == null)
            {
                certificate.Dispose();
                throw new CryptographicException();
            }
            return certificate;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("Data Protection requires a valid private-key PFX, correct password and more than seven days of certificate validity.");
        }
    }
}
