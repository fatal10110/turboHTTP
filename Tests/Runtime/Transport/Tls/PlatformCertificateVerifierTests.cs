using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #239: the native-verifier seam of <see cref="ServerCertificateVerifier"/> that the iOS
    /// SecTrust and Android TrustManager bridges plug into. The bridges themselves only run on
    /// devices (#99); these tests drive the seam with fake bridges in the Editor and pin that every
    /// bridge failure is a rejection, never <see cref="CertificateVerificationStatus.Trusted"/>.
    /// </summary>
    [TestFixture]
    public class PlatformCertificateVerifierTests
    {
        private const string Host = "example.com";
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // DER-shaped placeholders (SEQUENCE); the seam does not parse certificates.
        private static readonly byte[] Leaf = { 0x30, 0x03, 0x02, 0x01, 0x01 };
        private static readonly byte[] Intermediate = { 0x30, 0x03, 0x02, 0x01, 0x02 };
        private static readonly byte[] Root = { 0x30, 0x03, 0x02, 0x01, 0x03 };

        private byte[][] _savedAnchors;
        private ManualResetEventSlim _release;

        [SetUp]
        public void SetUp()
        {
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
            _release = new ManualResetEventSlim(false);
        }

        [TearDown]
        public void TearDown()
        {
            // Lets abandoned fake bridges return; they hold no shared state.
            _release.Set();
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        private static PlatformVerifierLimits Limits(int timeoutMs = 30000, int maxAbandoned = 4)
            => new PlatformVerifierLimits(TimeSpan.FromMilliseconds(timeoutMs), maxAbandoned);

        private static CertificateVerificationResult Verify(
            PlatformCertificateVerifier bridge, PlatformVerifierLimits limits = null,
            string host = Host, byte[][] chain = null)
            => ServerCertificateVerifier.Verify(chain ?? new[] { Leaf, Intermediate }, host, Now,
                requiresPlatformVerifier: true, bridge, limits ?? Limits());

        private static PlatformCertificateVerifier Returns(CertificateVerificationStatus status, byte[][] path)
            => (chain, host, time) => new CertificateVerificationResult(status, "bridge detail", path);

        private static void AssertRejected(CertificateVerificationStatus expected, CertificateVerificationResult result)
        {
            Assert.AreEqual(expected, result.Status, "Detail: " + result.Detail);
            Assert.IsNull(result.Path, "a rejection carries no path");
        }

        [Test]
        public void NoRegisteredBridge_IsNoVerifier()
        {
            AssertRejected(CertificateVerificationStatus.NoVerifier, Verify(null));
        }

        [Test]
        public void TrustedBridge_ReturnsPlatformPath_OnWorkerWithCopiedChainAndHost()
        {
            var serverChain = new[] { Leaf, Intermediate };
            byte[][] seenChain = null;
            string seenHost = null;
            DateTime seenTime = default;
            int bridgeThread = 0;
            var built = new[] { (byte[])Leaf.Clone(), (byte[])Intermediate.Clone(), (byte[])Root.Clone() };

            var result = Verify((chain, host, time) =>
            {
                seenChain = chain;
                seenHost = host;
                seenTime = time;
                bridgeThread = Thread.CurrentThread.ManagedThreadId;
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, built);
            }, chain: serverChain);

            Assert.AreEqual(CertificateVerificationStatus.Trusted, result.Status, "Detail: " + result.Detail);
            Assert.AreSame(built, result.Path);
            Assert.AreEqual(Host, seenHost);
            Assert.AreEqual(Now, seenTime);
            Assert.AreEqual(2, seenChain.Length);
            Assert.AreNotSame(serverChain[0], seenChain[0], "the bridge gets a private copy");
            CollectionAssert.AreEqual(Leaf, seenChain[0]);
            Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, bridgeThread, "the bridge runs on a dedicated worker");
        }

        [Test]
        public void ThrowingBridge_IsInvalid()
        {
            var result = Verify((chain, host, time) => throw new InvalidOperationException("JNI failure"));

            AssertRejected(CertificateVerificationStatus.Invalid, result);
            StringAssert.Contains("InvalidOperationException", result.Detail);
        }

        [Test]
        public void HangingBridge_TimesOut_IsInvalid()
        {
            var release = _release;
            var result = Verify((chain, host, time) =>
            {
                release.Wait();
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            }, Limits(timeoutMs: 50));

            AssertRejected(CertificateVerificationStatus.Invalid, result);
            StringAssert.Contains("timed out", result.Detail);
        }

        [Test]
        public void AbandonedBridgesAtLimit_RejectWithoutStartingAnother()
        {
            var release = _release;
            var limits = Limits(timeoutMs: 20, maxAbandoned: 2);
            PlatformCertificateVerifier hanging = (chain, host, time) =>
            {
                release.Wait();
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            };

            AssertRejected(CertificateVerificationStatus.Invalid, Verify(hanging, limits));
            AssertRejected(CertificateVerificationStatus.Invalid, Verify(hanging, limits));
            Assert.AreEqual(2, limits.Abandoned);

            int calls = 0;
            var result = Verify((chain, host, time) =>
            {
                Interlocked.Increment(ref calls);
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            }, limits);

            AssertRejected(CertificateVerificationStatus.Invalid, result);
            StringAssert.Contains("unresponsive", result.Detail);
            Assert.AreEqual(0, Volatile.Read(ref calls));
        }

        private static IEnumerable<TestCaseData> BadTrustedPaths()
        {
            yield return new TestCaseData((object)null).SetName("TrustedBridge_NullPath_IsInvalid");
            yield return new TestCaseData((object)new byte[0][]).SetName("TrustedBridge_EmptyPath_IsInvalid");
            yield return new TestCaseData((object)new[] { Leaf, null }).SetName("TrustedBridge_NullElement_IsInvalid");
            yield return new TestCaseData((object)new[] { Leaf, new byte[0] }).SetName("TrustedBridge_EmptyElement_IsInvalid");
            yield return new TestCaseData((object)new[] { Leaf, new byte[] { 0x04, 0x01, 0x00 } })
                .SetName("TrustedBridge_NonDerElement_IsInvalid");
            yield return new TestCaseData((object)new[] { Intermediate, Root })
                .SetName("TrustedBridge_PathNotStartingAtServerLeaf_IsInvalid");

            var overlong = new byte[PlatformVerifierRunner.MaxPathLength + 1][];
            for (int i = 0; i < overlong.Length; i++)
                overlong[i] = Leaf;
            yield return new TestCaseData((object)overlong).SetName("TrustedBridge_OverlongPath_IsInvalid");
        }

        [TestCaseSource(nameof(BadTrustedPaths))]
        public void TrustedBridge_WithBadPath_IsInvalid(byte[][] path)
        {
            AssertRejected(CertificateVerificationStatus.Invalid,
                Verify(Returns(CertificateVerificationStatus.Trusted, path)));
        }

        // Test parameters are ints: the status enum is internal and NUnit test methods are public.
        [TestCase((int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase((int)CertificateVerificationStatus.NotTimeValid)]
        [TestCase((int)CertificateVerificationStatus.Invalid)]
        [TestCase((int)CertificateVerificationStatus.NoVerifier)]
        public void RejectingBridge_PassesStatusThrough_WithoutPath(int statusValue)
        {
            var status = (CertificateVerificationStatus)statusValue;
            var result = Verify(Returns(status, new[] { Leaf, Root }));

            AssertRejected(status, result);
            Assert.AreEqual("bridge detail", result.Detail);
        }

        [Test]
        public void UnknownBridgeStatus_IsInvalid()
        {
            AssertRejected(CertificateVerificationStatus.Invalid,
                Verify(Returns((CertificateVerificationStatus)99, new[] { Leaf })));
        }

        [TestCase(null)]
        [TestCase("")]
        public void MissingHost_IsInvalid_BridgeNotCalled(string host)
        {
            int calls = 0;
            var result = Verify((chain, h, time) =>
            {
                Interlocked.Increment(ref calls);
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            }, host: host);

            AssertRejected(CertificateVerificationStatus.Invalid, result);
            Assert.AreEqual(0, Volatile.Read(ref calls));
        }

        [Test]
        public void EmptyCertificateInChain_IsInvalid_BridgeNotCalled()
        {
            int calls = 0;
            var result = Verify((chain, h, time) =>
            {
                Interlocked.Increment(ref calls);
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            }, chain: new[] { Leaf, new byte[0] });

            AssertRejected(CertificateVerificationStatus.Invalid, result);
            Assert.AreEqual(0, Volatile.Read(ref calls));
        }

        [Test]
        public void TestAnchorsRegistered_UseX509ChainPath_NotTheBridge()
        {
            ServerCertificateVerifier.AddTestTrustAnchor(Root);
            int calls = 0;

            // The placeholders are not certificates, so the X509Chain path rejects them; the point
            // is that the bridge is not consulted when test anchors exist.
            var result = Verify((chain, h, time) =>
            {
                Interlocked.Increment(ref calls);
                return new CertificateVerificationResult(CertificateVerificationStatus.Trusted, null, new[] { Leaf });
            });

            Assert.AreNotEqual(CertificateVerificationStatus.Trusted, result.Status);
            Assert.AreEqual(0, Volatile.Read(ref calls));
        }

        [Test]
        public void AppleBridge_TrustedBuffer_IsReadAsPath()
        {
            var path = new[] { Leaf, Root };
            var buffer = AllocPathBuffer(path, out int length);
            try
            {
                var result = AppleTrustVerifier.FromNative(AppleTrustVerifier.ResultTrusted, 0, buffer, length);

                Assert.AreEqual(CertificateVerificationStatus.Trusted, result.Status);
                Assert.AreEqual(2, result.Path.Length);
                CollectionAssert.AreEqual(Leaf, result.Path[0]);
                CollectionAssert.AreEqual(Root, result.Path[1]);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void AppleBridge_MalformedBuffer_Throws_SoTheRunnerRejects()
        {
            var buffer = AllocPathBuffer(new[] { Leaf, Root }, out int length);
            try
            {
                Assert.Throws<FormatException>(() => AppleTrustVerifier.ReadPath(buffer, length - 1), "truncated");
                Assert.Throws<FormatException>(() => AppleTrustVerifier.ReadPath(IntPtr.Zero, length), "null");
                Marshal.WriteInt32(buffer, 0, PlatformVerifierRunner.MaxPathLength + 1);
                Assert.Throws<FormatException>(() => AppleTrustVerifier.ReadPath(buffer, length), "count");
                Marshal.WriteInt32(buffer, 0, 2);
                Marshal.WriteInt32(buffer, 4, int.MaxValue);
                Assert.Throws<FormatException>(() => AppleTrustVerifier.ReadPath(buffer, length), "length");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var malformed = Verify((chain, h, time) => AppleTrustVerifier.FromNative(
                AppleTrustVerifier.ResultTrusted, 0, IntPtr.Zero, 0));
            AssertRejected(CertificateVerificationStatus.Invalid, malformed);
        }

        [TestCase(AppleTrustVerifier.ResultUntrustedRoot, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(AppleTrustVerifier.ResultNotTimeValid, (int)CertificateVerificationStatus.NotTimeValid)]
        [TestCase(AppleTrustVerifier.ResultInvalid, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(AppleTrustVerifier.ResultBridgeError, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(-1, (int)CertificateVerificationStatus.Invalid)]
        public void AppleBridge_ResultCodes_MapToRejections(int code, int expectedValue)
        {
            var expected = (CertificateVerificationStatus)expectedValue;
            var result = AppleTrustVerifier.FromNative(code, -67843, IntPtr.Zero, 0);

            AssertRejected(expected, result);
            StringAssert.Contains("-67843", result.Detail);
        }

        private static IntPtr AllocPathBuffer(byte[][] path, out int length)
        {
            length = 4;
            foreach (var der in path)
                length += 4 + der.Length;

            var buffer = Marshal.AllocHGlobal(length);
            Marshal.WriteInt32(buffer, 0, path.Length);
            int offset = 4;
            foreach (var der in path)
            {
                Marshal.WriteInt32(buffer, offset, der.Length);
                offset += 4;
                Marshal.Copy(der, 0, IntPtr.Add(buffer, offset), der.Length);
                offset += der.Length;
            }

            return buffer;
        }
    }
}
