using System;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls
{
    /// <summary>Server certificate carrier interface.</summary>
    public interface TlsServerCertificate
    {
        Certificate Certificate { get; }

        CertificateStatus CertificateStatus { get; }
    }
}
