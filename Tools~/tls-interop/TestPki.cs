using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TurboHTTP.Tools.TlsInterop
{
    /// <summary>
    /// The interop job's throwaway PKI, generated per run with <see cref="CertificateRequest"/> (the job runs
    /// on .NET 8, not Unity Mono): a root, an intermediate, a rogue root and one PEM leaf per case, all leaves
    /// sharing one RSA 2048 key (<c>leaf.key</c>). Only <c>root.pem</c> becomes trusted, through
    /// <c>SSL_CERT_FILE</c> in the run phase, so the SslStream verifier code path is unchanged; BouncyCastle
    /// gets it as a test trust anchor (Program, #248: no platform verifier on Linux).
    /// </summary>
    internal static class TestPki
    {
        public const string RootFile = "root.pem";
        public const string KeyFile = "leaf.key";
        public const string IntermediateFile = "intermediate.pem";

        private static readonly DateTimeOffset CaFrom = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset CaTo = new DateTimeOffset(2126, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset LeafFrom = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset LeafTo = new DateTimeOffset(2125, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>The PEM file of the leaf <paramref name="name"/> (see <see cref="Generate"/>).</summary>
        public static string Leaf(string dir, string name) => Path.Combine(dir, "leaf-" + name + ".pem");

        public static void Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            using var rootKey = RSA.Create(2048);
            using var interKey = RSA.Create(2048);
            using var rogueKey = RSA.Create(2048);
            using var leafKey = RSA.Create(2048);

            using var root = CreateCa("CN=TurboHTTP Interop Root", rootKey, null);
            using var inter = CreateCa("CN=TurboHTTP Interop Intermediate", interKey, root);
            using var rogue = CreateCa("CN=TurboHTTP Interop Rogue Root", rogueKey, null);

            File.WriteAllText(Path.Combine(dir, RootFile), root.ExportCertificatePem() + "\n");
            File.WriteAllText(Path.Combine(dir, IntermediateFile), inter.ExportCertificatePem() + "\n");
            File.WriteAllText(Path.Combine(dir, KeyFile), leafKey.ExportPkcs8PrivateKeyPem() + "\n");

            var local = San(b =>
            {
                b.AddDnsName("localhost");
                b.AddIpAddress(IPAddress.Loopback);
                b.AddIpAddress(IPAddress.IPv6Loopback);
            });

            WriteLeaf(dir, "valid", "CN=localhost", leafKey, root, local, LeafFrom, LeafTo);
            WriteLeaf(dir, "via-intermediate", "CN=localhost", leafKey, inter, local, LeafFrom, LeafTo);
            WriteLeaf(dir, "expired", "CN=localhost", leafKey, root, local,
                new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2002, 1, 1, 0, 0, 0, TimeSpan.Zero));
            WriteLeaf(dir, "not-yet-valid", "CN=localhost", leafKey, root, local,
                new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), LeafTo);
            WriteLeaf(dir, "wrong-host", "CN=wrong.example", leafKey, root, San(b => b.AddDnsName("wrong.example")), LeafFrom, LeafTo);
            WriteLeaf(dir, "tld-wildcard", "CN=*.com", leafKey, root, San(b => b.AddDnsName("*.com")), LeafFrom, LeafTo);
            WriteLeaf(dir, "cn-only-with-san", "CN=localhost", leafKey, root, San(b => b.AddDnsName("wrong.example")), LeafFrom, LeafTo);
            WriteLeaf(dir, "ip-in-dns-san", "CN=127.0.0.1", leafKey, root, San(b => b.AddDnsName("127.0.0.1")), LeafFrom, LeafTo);
            WriteLeaf(dir, "untrusted-root", "CN=localhost", leafKey, rogue, local, LeafFrom, LeafTo);
            WriteLeaf(dir, "self-signed", "CN=localhost", leafKey, null, local, LeafFrom, LeafTo);
        }

        private static X509Certificate2 CreateCa(string subject, RSA key, X509Certificate2 issuer)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            if (issuer == null)
                return request.CreateSelfSigned(CaFrom, CaTo);

            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            using var signed = request.Create(issuer, CaFrom, CaTo, Serial());
            return signed.CopyWithPrivateKey(key);
        }

        private static void WriteLeaf(string dir, string name, string subject, RSA key, X509Certificate2 issuer,
            X509Extension san, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            request.CertificateExtensions.Add(san);
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            X509Certificate2 cert;
            if (issuer == null)
            {
                cert = request.CreateSelfSigned(notBefore, notAfter);
            }
            else
            {
                request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
                cert = request.Create(issuer, notBefore, notAfter, Serial());
            }

            using (cert)
                File.WriteAllText(Leaf(dir, name), cert.ExportCertificatePem() + "\n");
        }

        private static X509Extension San(Action<SubjectAlternativeNameBuilder> fill)
        {
            var builder = new SubjectAlternativeNameBuilder();
            fill(builder);
            return builder.Build();
        }

        private static byte[] Serial()
        {
            var serial = new byte[16];
            RandomNumberGenerator.Fill(serial);
            serial[0] &= 0x7F;
            return serial;
        }
    }
}
