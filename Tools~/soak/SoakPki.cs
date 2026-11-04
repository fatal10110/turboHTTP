using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TurboHTTP.Tools.Soak
{
    /// <summary>
    /// Per-run throwaway PKI: a root and one leaf for 127.0.0.1 (iPAddress SAN, #274) and localhost. Only
    /// <c>root.pem</c> is trusted, through <c>SSL_CERT_FILE</c> in run.sh (the tls-interop precedent), so the
    /// client's SslStream verification runs unchanged; nothing in the harness accepts an untrusted chain.
    /// </summary>
    internal static class SoakPki
    {
        public const string RootFile = "root.pem";
        public const string LeafFile = "leaf.pem";
        public const string KeyFile = "leaf.key";

        public static void Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            var from = DateTimeOffset.UtcNow.AddDays(-1);
            var to = DateTimeOffset.UtcNow.AddDays(30);
            using var rootKey = RSA.Create(2048);
            using var leafKey = RSA.Create(2048);

            var rootRequest = new CertificateRequest("CN=TurboHTTP Soak Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            using var root = rootRequest.CreateSelfSigned(from, to);

            var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
            leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            serial[0] &= 0x7F;
            using var leaf = leafRequest.Create(root, from, to, serial);

            File.WriteAllText(Path.Combine(dir, RootFile), root.ExportCertificatePem() + "\n");
            File.WriteAllText(Path.Combine(dir, LeafFile), leaf.ExportCertificatePem() + "\n");
            File.WriteAllText(Path.Combine(dir, KeyFile), leafKey.ExportPkcs8PrivateKeyPem() + "\n");
        }

        /// <summary>The server certificate with its key, re-imported from PKCS#12 so SslStream can use it as a server.</summary>
        public static X509Certificate2 LoadServerCertificate(string dir)
        {
            using var pem = X509Certificate2.CreateFromPemFile(Path.Combine(dir, LeafFile), Path.Combine(dir, KeyFile));
            return new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
        }
    }
}
