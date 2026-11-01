using System;
using System.Collections.Generic;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.X509;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// Server certificate authentication for the BouncyCastle provider (#33).
    /// </summary>
    /// <remarks>
    /// <para>Order, each step failing closed with a fatal alert the provider maps to
    /// <c>AuthenticationException</c> (and the transport to <c>UHttpErrorType.CertificateError</c>):</para>
    /// <list type="number">
    /// <item>Host identity (RFC 6125 section 6) against the leaf's subjectAltName, CN only without
    /// one: <see cref="TlsCertificateNameMatcher"/>. Mismatch: bad_certificate.</item>
    /// <item>Leaf extended key usage, when present, includes serverAuth (RFC 5280 4.2.1.12).
    /// Otherwise: bad_certificate.</item>
    /// <item>Leaf validity period. Outside it: certificate_expired.</item>
    /// <item>Path validation outside BouncyCastle (decision A on #141):
    /// <see cref="ServerCertificateVerifier"/> on the DER chain. Untrusted: unknown_ca; time:
    /// certificate_expired; no verifier on this platform: certificate_unknown; anything else:
    /// bad_certificate.</item>
    /// <item><see cref="TlsUtilities.CheckPeerSigAlgs"/> on the path the platform built (leaf to
    /// anchor), so every certificate signature in it uses an algorithm this client offered.</item>
    /// </list>
    /// <para>Runs synchronously on the handshake thread inside BouncyCastle's input processing.
    /// One instance per handshake; no shared state besides the verifier's immutable anchor snapshot.
    /// Every BouncyCastle API used for certificate checks stays in this file: the vendored-library
    /// strip computes its kept closure from it (Runtime/Transport/BouncyCastle/README.md).</para>
    /// </remarks>
    internal sealed class TurboTlsAuthentication : TlsAuthentication
    {
        private readonly TlsContext _context;
        private readonly string _targetHost;

        public TurboTlsAuthentication(TlsContext context, string targetHost)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            // No client certificate support.
            return null;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (serverCertificate == null || serverCertificate.Certificate == null || serverCertificate.Certificate.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.certificate_unknown,
                    "Server did not provide a certificate");
            }

            var chain = serverCertificate.Certificate;
            if (chain.Length > ServerCertificateVerifier.MaxChainLength)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    $"Server certificate chain longer than {ServerCertificateVerifier.MaxChainLength}");
            }

            var chainDer = new byte[chain.Length][];
            for (int i = 0; i < chainDer.Length; i++)
                chainDer[i] = chain.GetCertificateAt(i).GetEncoded();

            var now = DateTime.UtcNow;
            ValidateLeaf(chainDer[0], _targetHost, now);

            var builtPath = VerifyPath(chainDer, now);

            CheckPathSignatureAlgorithms(builtPath);
        }

        /// <summary>
        /// Host identity, EKU and validity of the leaf. Throws <see cref="TlsFatalAlert"/>.
        /// </summary>
        private static void ValidateLeaf(byte[] leafDer, string targetHost, DateTime nowUtc)
        {
            var cert = ParseCertificate(leafDer);
            ValidateHostname(cert, targetHost);
            ValidateExtendedKeyUsage(cert);
            ValidateValidity(cert, nowUtc);
        }

        private static X509Certificate ParseCertificate(byte[] der)
        {
            try
            {
                return new X509Certificate(der);
            }
            catch (Exception ex) when (!(ex is TlsFatalAlert))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate, "Malformed server certificate", ex);
            }
        }

        private static void ValidateHostname(X509Certificate cert, string targetHost)
        {
            bool hasSan;
            var dnsNames = new List<string>();
            var ipAddresses = new List<byte[]>();
            IList<string> commonNames;

            try
            {
                var generalNames = cert.GetSubjectAlternativeNameExtension();
                hasSan = generalNames != null;
                if (hasSan)
                {
                    foreach (var name in generalNames.GetNames())
                    {
                        switch (name.TagNo)
                        {
                            case GeneralName.DnsName:
                                dnsNames.Add(((IAsn1String)name.Name).GetString());
                                break;
                            case GeneralName.IPAddress:
                                ipAddresses.Add(Asn1OctetString.GetInstance(name.Name).GetOctets());
                                break;
                        }
                    }
                }

                commonNames = cert.SubjectDN.GetValueList(X509Name.CN);
            }
            catch (Exception ex) when (!(ex is TlsFatalAlert))
            {
                // A name extension the parser rejects fails the handshake; it never reads as "no names".
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    "Malformed subjectAltName or subject in server certificate", ex);
            }

            if (!TlsCertificateNameMatcher.Matches(targetHost, hasSan, dnsNames, ipAddresses, ToReadOnly(commonNames)))
            {
                // Names only, no other certificate content; host names are not credentials.
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    $"Server certificate does not match host '{targetHost}' " +
                    $"(subjectAltName dNSName: {string.Join(", ", dnsNames)}; iPAddress entries: {ipAddresses.Count})");
            }
        }

        private static IReadOnlyList<string> ToReadOnly(IList<string> values)
        {
            if (values == null)
                return Array.Empty<string>();
            return values as IReadOnlyList<string> ?? new List<string>(values);
        }

        private static void ValidateExtendedKeyUsage(X509Certificate cert)
        {
            IList<DerObjectIdentifier> usages;
            try
            {
                usages = cert.GetExtendedKeyUsage();
            }
            catch (Exception ex) when (!(ex is TlsFatalAlert))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    "Malformed extended key usage in server certificate", ex);
            }

            if (usages == null)
                return;

            foreach (var usage in usages)
            {
                if (usage.Equals(KeyPurposeID.id_kp_serverAuth) || usage.Equals(KeyPurposeID.AnyExtendedKeyUsage))
                    return;
            }

            throw new TlsFatalAlert(AlertDescription.bad_certificate,
                "Server certificate extended key usage does not permit serverAuth");
        }

        private static void ValidateValidity(X509Certificate cert, DateTime nowUtc)
        {
            if (nowUtc < cert.NotBefore.ToUniversalTime())
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Server certificate not yet valid (NotBefore: {cert.NotBefore.ToUniversalTime():u})");
            }

            if (nowUtc > cert.NotAfter.ToUniversalTime())
            {
                throw new TlsFatalAlert(AlertDescription.certificate_expired,
                    $"Server certificate expired (NotAfter: {cert.NotAfter.ToUniversalTime():u})");
            }
        }

        /// <summary>
        /// Runs the platform verifier and maps its outcome to a fatal alert. Returns the built path
        /// (leaf first, anchor last).
        /// </summary>
        private static byte[][] VerifyPath(byte[][] chainDer, DateTime nowUtc)
        {
            var result = ServerCertificateVerifier.Verify(chainDer, nowUtc);
            switch (result.Status)
            {
                case CertificateVerificationStatus.Trusted:
                    return result.Path;
                case CertificateVerificationStatus.UntrustedRoot:
                    throw new TlsFatalAlert(AlertDescription.unknown_ca,
                        "Server certificate chain is not trusted: " + result.Detail);
                case CertificateVerificationStatus.NotTimeValid:
                    throw new TlsFatalAlert(AlertDescription.certificate_expired,
                        "Server certificate chain is outside its validity period: " + result.Detail);
                case CertificateVerificationStatus.NoVerifier:
                    throw new TlsFatalAlert(AlertDescription.certificate_unknown, result.Detail);
                default:
                    throw new TlsFatalAlert(AlertDescription.bad_certificate,
                        "Server certificate chain is invalid: " + result.Detail);
            }
        }

        /// <summary>
        /// RFC 8446 4.4.2.2 / RFC 5246 7.4.2: the certificate signatures on the verified path must
        /// use algorithms the client offered. Keys outside the restricted factory (RSA 2048-8192,
        /// EC P-256/384/521) surface as ArgumentException or unsupported_certificate.
        /// </summary>
        private void CheckPathSignatureAlgorithms(byte[][] builtPath)
        {
            try
            {
                var path = new TlsCertificate[builtPath.Length];
                for (int i = 0; i < builtPath.Length; i++)
                    path[i] = _context.Crypto.CreateCertificate(builtPath[i]);

                TlsUtilities.CheckPeerSigAlgs(_context, path);
            }
            catch (TlsFatalAlert)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TlsFatalAlert(AlertDescription.unsupported_certificate,
                    "Server certificate path uses an unsupported key or signature algorithm", ex);
            }
        }
    }
}
