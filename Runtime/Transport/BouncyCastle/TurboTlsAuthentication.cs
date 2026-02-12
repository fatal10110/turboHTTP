

using System;
using System.Linq;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.X509;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// TLS authentication handler for server certificate validation.
    /// </summary>
    /// <remarks>
    /// <b>SECURITY WARNING — NON-PRODUCTION:</b> This implementation performs hostname and
    /// validity-period checks only. Certificate chain validation (signature verification,
    /// trust anchor matching, revocation) is NOT implemented. This means any self-signed or
    /// forged certificate with correct hostname/dates will be accepted, leaving connections
    /// vulnerable to MITM attacks. Full chain validation is deferred to Phase 6.
    /// Do NOT use BouncyCastle TLS backend in production until Phase 6 is complete.
    /// </remarks>
    internal sealed class TurboTlsAuthentication : TlsAuthentication
    {
        private readonly string _targetHost;

        public TurboTlsAuthentication(string targetHost)
        {
            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            // No client certificate support in this phase
            return null;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (serverCertificate == null || serverCertificate.Certificate.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "Server did not provide a certificate");
            }

            var chain = serverCertificate.Certificate;
            var leafCert = chain.GetCertificateAt(0);

            // 1. Validate certificate chain
            ValidateCertificateChain(chain);

            // 2. Verify hostname matches certificate
            ValidateHostname(leafCert, _targetHost);

            // 3. Check certificate validity period
            ValidateValidity(leafCert);
        }

        private void ValidateCertificateChain(Certificate chain)
        {
            if (chain.Length == 0)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "Certificate chain is empty");
            }
            // TODO: Full chain validation deferred to Phase 6
#pragma warning disable CS0168
#warning "Phase 3C: Certificate chain validation is NOT implemented — accepting any chain with valid hostname/dates. See Phase 6."
#pragma warning restore CS0168
        }

        private void ValidateHostname(TlsCertificate tlsCertificate, string hostname)
        {
            var cert = new X509Certificate(tlsCertificate.GetEncoded());
            
            var sans = GetSubjectAlternativeNames(cert);
            
            if (sans.Any(san => MatchesHostname(san, hostname)))
            {
                return;
            }

            var subject = cert.SubjectDN.ToString();
            var cn = ExtractCommonName(subject);
            if (cn != null && MatchesHostname(cn, hostname))
            {
                return;
            }

            throw new TlsFatalAlert(AlertDescription.bad_certificate,
                $"Certificate hostname mismatch: expected '{hostname}', " +
                $"certificate valid for: {string.Join(", ", sans)}");
        }

        private void ValidateValidity(TlsCertificate tlsCertificate)
        {
            var cert = new X509Certificate(tlsCertificate.GetEncoded());
            var now = DateTime.UtcNow;

            if (now < cert.NotBefore)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Certificate not yet valid (NotBefore: {cert.NotBefore})");
            }

            if (now > cert.NotAfter)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Certificate expired (NotAfter: {cert.NotAfter})");
            }
        }

        private string[] GetSubjectAlternativeNames(X509Certificate cert)
        {
            try
            {
                var sans = cert.GetSubjectAlternativeNames();
                if (sans == null)
                    return Array.Empty<string>();

                var result = new System.Collections.Generic.List<string>();
                foreach (var san in sans)
                {
                    if (san.Count >= 2 && san[0] is int type && type == 2)
                    {
                        result.Add(san[1].ToString());
                    }
                }
                return result.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private string ExtractCommonName(string subject)
        {
            var parts = subject.Split(',');
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2 && kv[0].Trim().Equals("CN", StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Trim();
                }
            }
            return null;
        }

        private bool MatchesHostname(string pattern, string hostname)
        {
            if (string.Equals(pattern, hostname, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith("*."))
            {
                var suffix = pattern.Substring(2);
                var dotIndex = hostname.IndexOf('.');
                
                if (dotIndex > 0)
                {
                    var hostSuffix = hostname.Substring(dotIndex + 1);
                    return string.Equals(suffix, hostSuffix, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
    }
}
