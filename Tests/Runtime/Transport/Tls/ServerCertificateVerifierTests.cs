using System;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using Certs = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;
using Public = TurboHTTP.Tests.Transport.Tls.PublicChainTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #33: path validation of <see cref="ServerCertificateVerifier"/> (the <see cref="X509Chain"/>
    /// path used by the BouncyCastle provider in the Editor and on desktop) on committed ECDSA test
    /// chains. The P-384 and name-constraint cases are the decision-A check on #141: they decide
    /// whether Unity Mono's <see cref="X509Chain"/> is fit for the desktop path. Their result is
    /// owed on the host-editor run; a failure there reopens the desktop choice.
    /// </summary>
    [TestFixture]
    public class ServerCertificateVerifierTests
    {
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        private static CertificateVerificationResult Verify(params byte[][] chain)
            => ServerCertificateVerifier.Verify(chain, "test.local", Now);

        private static void AssertStatus(CertificateVerificationStatus expected, CertificateVerificationResult result)
        {
            Assert.AreEqual(expected, result.Status, "Detail: " + result.Detail);
        }

        [Test]
        public void P256Chain_ToTestAnchor_IsTrusted_AndPathEndsAtAnchor()
        {
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
            // AllowUnknownCertificateAuthority is set while an anchor exists; the root still has to
            // be that anchor.
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.UntrustedRoot, Verify(Certs.LeafFromRogue, Certs.RogueRoot));
        }

        [Test]
        public void SelfSignedLeaf_WithoutAnchor_IsUntrusted()
        {
            using var localhost = TestLocalhostCertificate.TryLoad();
            if (localhost == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            // TryLoad registered the anchor; drop it again for this case.
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);

            AssertStatus(CertificateVerificationStatus.UntrustedRoot, Verify(localhost.RawData));
        }

        [Test]
        public void ExpiredLeaf_IsNotTimeValid()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.NotTimeValid, Verify(Certs.LeafExpired));
        }

        [Test]
        public void NotYetValidLeaf_IsNotTimeValid()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.NotTimeValid, Verify(Certs.LeafNotYetValid));
        }

        [Test]
        public void NameConstraints_PermittedLeaf_IsTrusted()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Trusted, Verify(Certs.LeafPermitted, Certs.ConstrainedCa));
        }

        [Test]
        public void NameConstraints_ExcludedLeaf_IsInvalid()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Invalid, Verify(Certs.LeafExcluded, Certs.ConstrainedCa));
        }

        [Test]
        public void ServerAuthLeaf_UnderClientAuthOnlyIntermediate_IsInvalid()
        {
            // EKU is chained: SslStream would reject this path, so the BC path must too.
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            AssertStatus(CertificateVerificationStatus.Invalid,
                Verify(Certs.LeafUnderClientAuthOnlyCa, Certs.ClientAuthOnlyCa));
        }

        [Test]
        public void PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore()
        {
            // The production configuration: no test anchors (SetUp cleared them), NoFlag, platform
            // root store. The root is not committed, so Trusted means the store supplied it. The
            // host-editor outcome on Unity Mono is recorded on #239.
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
