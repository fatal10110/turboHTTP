using System;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using Certs = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;
using Public = TurboHTTP.Tests.Transport.Tls.PublicChainTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #33, #248: path validation of <see cref="ServerCertificateVerifier"/> in its production
    /// configuration on committed ECDSA test chains. On Windows and macOS this is the native
    /// desktop verifier (crypt32, SecTrust), which takes the test anchors as its only roots and also
    /// applies the platform SSL host-name policy, so each case passes the host its leaf names.
    /// Linux has no verifier (BouncyCastle unsupported, #248): the fixture is skipped there.
    /// </summary>
    [TestFixture]
    public class ServerCertificateVerifierTests
    {
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            if (ServerCertificateVerifier.CurrentPlatform == VerifierPlatform.Linux)
                TestRequirements.IgnoreEnvironment("BouncyCastle has no certificate verifier on Linux (#248).");

            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        // The committed leaves name localhost unless a case passes its own host.
        private static CertificateVerificationResult Verify(params byte[][] chain)
            => VerifyFor("localhost", chain);

        private static CertificateVerificationResult VerifyFor(string host, params byte[][] chain)
            => ServerCertificateVerifier.Verify(chain, host, Now);

        private static void AssertStatus(CertificateVerificationStatus expected, CertificateVerificationResult result)
        {
            Assert.AreEqual(expected, result.Status, "Detail: " + result.Detail);
        }

        /// <summary>
        /// Cases that register a test anchor and expect a path-validation verdict. Only the desktop
        /// verifiers take test anchors; elsewhere (Android/iOS players) the anchors go to the
        /// managed X509Chain, which Mono/IL2CPP cannot evaluate for these ECDSA chains
        /// (NotSupportedException, #321). Production on those platforms uses the native
        /// TrustManager/SecTrust verifiers (#253), which the device-only fixtures cover.
        /// </summary>
        private static void RequireTestAnchorVerifier()
        {
            if (!ServerCertificateVerifier.PlatformVerifierTrustsTestAnchors)
                TestRequirements.IgnoreEnvironment(
                    "Test anchors need the Windows or macOS desktop verifier; this platform evaluates them with " +
                    "the managed X509Chain, which cannot validate the ECDSA test chains (#321).");
        }

        [Test]
        public void P256Chain_ToTestAnchor_IsTrusted_AndPathEndsAtAnchor()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            var result = Verify(Certs.LeafLocalhost);

            AssertStatus(CertificateVerificationStatus.Trusted, result);
            Assert.AreEqual(2, result.Path.Length, "leaf + anchor");
            CollectionAssert.AreEqual(Certs.LeafLocalhost, result.Path[0]);
            CollectionAssert.AreEqual(Certs.RootA, result.Path[1]);
        }

        [Test]
        public void P384Chain_ToTestAnchor_IsTrusted()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootB);

            AssertStatus(CertificateVerificationStatus.Trusted, Verify(Certs.LeafP384));
        }

        [Test]
        public void NoAnchor_UnknownRoot_IsUntrusted()
        {
            // The production configuration: no test anchor, Root A is in no platform store.
            AssertStatus(CertificateVerificationStatus.UntrustedRoot, Verify(Certs.LeafLocalhost));
        }

        [Test]
        public void ServerSentRootIsNotAnAnchor()
        {
            // A server cannot make its own root trusted by sending it in the chain.
            AssertStatus(CertificateVerificationStatus.UntrustedRoot, Verify(Certs.LeafLocalhost, Certs.RootA));
        }

        [Test]
        public void RogueRoot_WithOtherAnchorRegistered_IsUntrusted()
        {
            RequireTestAnchorVerifier();
            // AllowUnknownCertificateAuthority is set while an anchor exists; the root still has to
            // be that anchor.
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.UntrustedRoot, Verify(Certs.LeafFromRogue, Certs.RogueRoot));
        }

        // The short-lived certificate (#332): Apple rejects a long validity period on a certificate
        // issued today (-67901, Invalid) before it would report the missing anchor, and loading it
        // registers no test anchor. Verified at a time inside its validity, so only the anchor is missing.
        [Test]
        public void SelfSignedLeaf_WithoutAnchor_IsUntrusted()
        {
            using var localhost = TestShortLivedLocalhostCertificate.TryLoad();
            if (localhost == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestShortLivedLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            // No test anchor is registered (SetUp cleared them; this certificate registers none).
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
            DateTime notBefore = localhost.NotBefore.ToUniversalTime();
            DateTime insideValidity = notBefore + TimeSpan.FromTicks((localhost.NotAfter.ToUniversalTime() - notBefore).Ticks / 2);

            AssertStatus(
                CertificateVerificationStatus.UntrustedRoot,
                ServerCertificateVerifier.Verify(new[] { localhost.RawData }, "localhost", insideValidity));
        }

        [Test]
        public void ExpiredLeaf_IsNotTimeValid()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.NotTimeValid, Verify(Certs.LeafExpired));
        }

        [Test]
        public void NotYetValidLeaf_IsNotTimeValid()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.NotTimeValid, Verify(Certs.LeafNotYetValid));
        }

        [Test]
        public void NameConstraints_PermittedLeaf_IsTrusted()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Trusted,
                VerifyFor("www.allowed.test", Certs.LeafPermitted, Certs.ConstrainedCa));
        }

        [Test]
        public void NameConstraints_ExcludedLeaf_IsInvalid()
        {
            RequireTestAnchorVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            var result = VerifyFor("www.forbidden.test", Certs.LeafExcluded, Certs.ConstrainedCa);

            AssertStatus(CertificateVerificationStatus.Invalid, result);
            if (ServerCertificateVerifier.CurrentPlatform == VerifierPlatform.Windows)
                StringAssert.Contains("name constraints", result.Detail, "rejected for the constraint, not for building");
        }

        [Test]
        public void ServerAuthLeaf_UnderClientAuthOnlyIntermediate_IsInvalid()
        {
            RequireTestAnchorVerifier();
            // EKU is chained: SslStream would reject this path, so the BC path must too.
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Invalid,
                Verify(Certs.LeafUnderClientAuthOnlyCa, Certs.ClientAuthOnlyCa));
        }

        [Test]
        public void PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore()
        {
            // The production configuration: no test anchors (SetUp cleared them), the platform root
            // store. The root is not committed, so Trusted means the store supplied it (#248).
            var at = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.That(at, Is.InRange(Public.LeafNotBeforeUtc, Public.LeafNotAfterUtc));

            var result = ServerCertificateVerifier.Verify(new[] { Public.Leaf, Public.Intermediate }, "www.digicert.com", at);

            AssertStatus(CertificateVerificationStatus.Trusted, result);
            Assert.AreEqual(3, result.Path.Length, "leaf + intermediate + platform root");
            CollectionAssert.AreEqual(Public.Leaf, result.Path[0]);
            CollectionAssert.AreEqual(Public.Intermediate, result.Path[1]);
            using var root = new X509Certificate2(result.Path[2]);
            StringAssert.Contains("CN=" + Public.RootCommonName, root.Subject);
            Assert.AreEqual(root.Subject, root.Issuer, "the path ends at a self-signed root");
        }

        [Test]
        public void MalformedCertificate_IsInvalid()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Invalid, Verify(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x00 }));
        }

        [Test]
        public void OverlongChain_IsRejectedUnparsed()
        {
            var chain = new byte[ServerCertificateVerifier.MaxChainLength + 1][];
            for (int i = 0; i < chain.Length; i++)
                chain[i] = Certs.LeafLocalhost;

            AssertStatus(CertificateVerificationStatus.Invalid, Verify(chain));
        }

        [Test]
        public void Policy_RevocationOff_FetchBoundedUnderHandshakeTimeout()
        {
            using var chain = new X509Chain();

            ServerCertificateVerifier.ConfigurePolicy(chain.ChainPolicy, Now, hasTestAnchors: false);

            Assert.AreEqual(X509RevocationMode.NoCheck, chain.ChainPolicy.RevocationMode);
            Assert.AreEqual(1, chain.ChainPolicy.ApplicationPolicy.Count, "EKU is chained");
            Assert.AreEqual(ServerCertificateVerifier.ServerAuthOid, chain.ChainPolicy.ApplicationPolicy[0].Value);
            Assert.AreEqual(X509VerificationFlags.NoFlag, chain.ChainPolicy.VerificationFlags,
                "Without test anchors nothing relaxes the platform policy.");
            Assert.Greater(chain.ChainPolicy.UrlRetrievalTimeout, TimeSpan.Zero);
            Assert.Less(chain.ChainPolicy.UrlRetrievalTimeout.TotalMilliseconds, 30000,
                "Certificate evaluation cannot be interrupted by the handshake deadline; its own bound must be shorter.");
        }
    }
}
