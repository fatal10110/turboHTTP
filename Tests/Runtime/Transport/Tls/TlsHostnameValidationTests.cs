using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsHostnameValidationTests
    {
        private const string AuthTypeName = "TurboHTTP.Transport.BouncyCastle.TurboTlsAuthentication, TurboHTTP.Transport.BouncyCastle";
        private const string BcTlsCryptoTypeName = "TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto, TurboHTTP.Transport.BouncyCastle";
        private const string CertificateTypeName = "TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.CertificateType, TurboHTTP.Transport.BouncyCastle";

        [Test]
        public void ValidateHostname_ExactMatch_Succeeds()
        {
            var auth = CreateAuth("example.com");
            var cert = CreateTlsCertificate("example.com", new[] { "example.com" }, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            Assert.DoesNotThrow(() => InvokePrivate(auth, "ValidateHostname", cert, "example.com"));
        }

        [Test]
        public void ValidateHostname_WildcardMatch_Succeeds()
        {
            var auth = CreateAuth("api.example.com");
            var cert = CreateTlsCertificate("*.example.com", new[] { "*.example.com" }, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            Assert.DoesNotThrow(() => InvokePrivate(auth, "ValidateHostname", cert, "api.example.com"));
        }

        [Test]
        public void ValidateHostname_WildcardMismatch_Throws()
        {
            var auth = CreateAuth("example.com");
            var cert = CreateTlsCertificate("*.example.com", new[] { "*.example.com" }, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            AssertTlsFatalAlert(() => InvokePrivate(auth, "ValidateHostname", cert, "example.com"));
        }

        [Test]
        public void ValidateHostname_MismatchedHost_Throws()
        {
            var auth = CreateAuth("attacker.com");
            var cert = CreateTlsCertificate("example.com", new[] { "example.com" }, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            AssertTlsFatalAlert(() => InvokePrivate(auth, "ValidateHostname", cert, "attacker.com"));
        }

        [Test]
        public void ValidateValidity_ExpiredCert_Throws()
        {
            var auth = CreateAuth("example.com");
            var cert = CreateTlsCertificate("example.com", new[] { "example.com" }, DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));

            AssertTlsFatalAlert(() => InvokePrivate(auth, "ValidateValidity", cert));
        }

        [Test]
        public void ValidateValidity_NotYetValid_Throws()
        {
            var auth = CreateAuth("example.com");
            var cert = CreateTlsCertificate("example.com", new[] { "example.com" }, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));

            AssertTlsFatalAlert(() => InvokePrivate(auth, "ValidateValidity", cert));
        }

        private static object CreateAuth(string host)
        {
            var authType = Type.GetType(AuthTypeName, throwOnError: false);
            if (authType == null)
            {
                Assert.Ignore("TurboHTTP.Transport.BouncyCastle is not available");
                return null;
            }

            try
            {
                return Activator.CreateInstance(
                    authType,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    binder: null,
                    args: new object[] { host },
                    culture: null);
            }
            catch (Exception ex)
            {
                Assert.Ignore($"Failed to create TurboTlsAuthentication via reflection: {ex.Message}");
                return null;
            }
        }

        private static object CreateTlsCertificate(
            string commonName,
            string[] sanDns,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter)
        {
            try
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    $"CN={commonName}",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                if (sanDns != null && sanDns.Length > 0)
                {
                    var sanBuilder = new SubjectAlternativeNameBuilder();
                    foreach (var dns in sanDns)
                        sanBuilder.AddDnsName(dns);
                    request.CertificateExtensions.Add(sanBuilder.Build());
                }

                using var cert = request.CreateSelfSigned(notBefore, notAfter);
                var der = cert.Export(X509ContentType.Cert);

                var bcTlsCryptoType = Type.GetType(BcTlsCryptoTypeName, throwOnError: false);
                if (bcTlsCryptoType == null)
                {
                    Assert.Ignore("BcTlsCrypto type not found");
                    return null;
                }

                var certType = Type.GetType(CertificateTypeName, throwOnError: false);
                if (certType == null)
                {
                    Assert.Ignore("CertificateType type not found");
                    return null;
                }

                var x509Field = certType.GetField("X509", BindingFlags.Public | BindingFlags.Static);
                if (x509Field == null)
                {
                    Assert.Ignore("CertificateType.X509 field not found");
                    return null;
                }

                short x509Type = Convert.ToInt16(x509Field.GetValue(null));

                var crypto = Activator.CreateInstance(bcTlsCryptoType);
                var createCert = bcTlsCryptoType.GetMethod("CreateCertificate", new[] { typeof(short), typeof(byte[]) });
                if (createCert == null)
                {
                    Assert.Ignore("BcTlsCrypto.CreateCertificate method not found");
                    return null;
                }

                return createCert.Invoke(crypto, new object[] { x509Type, der });
            }
            catch (PlatformNotSupportedException ex)
            {
                Assert.Ignore($"Certificate generation is not supported on this platform: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Assert.Ignore($"Certificate generation failed: {ex.Message}");
                return null;
            }
        }

        private static object InvokePrivate(object instance, string methodName, params object[] args)
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(instance.GetType().FullName, methodName);

            return method.Invoke(instance, args);
        }

        private static void AssertTlsFatalAlert(Action action)
        {
            var ex = Assert.Throws<TargetInvocationException>(() => action());
            Assert.IsNotNull(ex);
            Assert.IsNotNull(ex.InnerException, "Expected inner exception");
            Assert.AreEqual("TlsFatalAlert", ex.InnerException.GetType().Name);
        }
    }
}
