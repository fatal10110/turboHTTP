using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsProviderSelectorTests
    {
        [Test]
        public void ForceSslStream_ReturnsCorrectProvider()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }

        [Test]
        public void SslStream_IsAlpnSupported_ReturnsBoolean()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            // Does not throw - result depends on platform
            var supported = provider.IsAlpnSupported();
            Assert.IsNotNull(supported.GetType());
        }

        [Test]
        public void Auto_ReturnsNonNullProvider()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.IsNotNull(provider);
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [Test]
        public void Auto_OnDesktop_UsesSslStream()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }
#endif

#if UNITY_IOS || UNITY_ANDROID
        [Test]
        public void Auto_OnMobile_PrefersSslStream()
        {
            var autoProvider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.AreEqual("SslStream", autoProvider.ProviderName);
        }
#endif

        [Test]
        public void IsBouncyCastleAvailable_DoesNotThrow()
        {
            // Should not throw regardless of whether BouncyCastle is available
            bool available = TlsProviderSelector.IsBouncyCastleAvailable();
            // Just verify it returns a boolean without error
            Assert.IsInstanceOf<bool>(available);
        }

        [Test]
        public void ForceBouncyCastle_WhenNotAvailable_ThrowsInvalidOperationException()
        {
            if (TlsProviderSelector.IsBouncyCastleAvailable())
            {
                // BouncyCastle is bundled; verify it returns a provider instead of throwing
                var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
                Assert.IsNotNull(provider);
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
        }

        [Test]
        public void ForceBouncyCastle_WhenAvailable_ReturnsCorrectProvider()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                // BouncyCastle not available; verify it throws
                Assert.Throws<InvalidOperationException>(() =>
                    TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreEqual("BouncyCastle", provider.ProviderName);
        }

        [Test]
        public void ForceBouncyCastle_WhenAvailable_CachesInstance()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                // BouncyCastle not available; verify it throws
                Assert.Throws<InvalidOperationException>(() =>
                    TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
                return;
            }

            var provider1 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var provider2 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreSame(provider1, provider2, "BouncyCastle provider should be cached");
        }

        [Test]
        public void BouncyCastle_WhenAvailable_IsAlpnSupported_ReturnsTrue()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                // BouncyCastle not available; verify it throws
                Assert.Throws<InvalidOperationException>(() =>
                    TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.IsTrue(provider.IsAlpnSupported(), "BouncyCastle should always support ALPN");
        }

        // --- SslStream viability probe state tests ---

        [TearDown]
        public void ResetProbeState()
        {
            TlsProviderSelector.ResetProbeState();
        }

        [Test]
        public void IsSslStreamKnownBroken_InitiallyFalse()
        {
            Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken());
        }

        [Test]
        public void MarkSslStreamViable_SetsViableState()
        {
            TlsProviderSelector.MarkSslStreamViable();
            Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken());
        }

        [Test]
        public void MarkSslStreamBroken_SetsBrokenState()
        {
            TlsProviderSelector.MarkSslStreamBroken();
            Assert.IsTrue(TlsProviderSelector.IsSslStreamKnownBroken());
        }

        [Test]
        public void MarkSslStreamBroken_OverridesViable()
        {
            TlsProviderSelector.MarkSslStreamViable();
            TlsProviderSelector.MarkSslStreamBroken();
            Assert.IsTrue(TlsProviderSelector.IsSslStreamKnownBroken(),
                "Broken state should override viable — a platform failure after a lucky success is still broken");
        }

        [Test]
        public void MarkSslStreamViable_DoesNotOverrideBroken()
        {
            TlsProviderSelector.MarkSslStreamBroken();
            TlsProviderSelector.MarkSslStreamViable();
            Assert.IsTrue(TlsProviderSelector.IsSslStreamKnownBroken(),
                "Viable should not overwrite broken (CompareExchange only transitions from unknown)");
        }

        [Test]
        public void Auto_WhenSslStreamBroken_ReturnsBouncyCastle()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle not available in this build");
                return;
            }

            TlsProviderSelector.MarkSslStreamBroken();
            var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.AreEqual("BouncyCastle", provider.ProviderName);
        }

        [Test]
        public void Auto_WhenSslStreamBroken_AndNoBouncyCastle_ThrowsPlatformNotSupported()
        {
            if (TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is available — cannot test missing-fallback path");
                return;
            }

            TlsProviderSelector.MarkSslStreamBroken();
            Assert.Throws<PlatformNotSupportedException>(() =>
                TlsProviderSelector.GetProvider(TlsBackend.Auto));
        }

        [Test]
        public void ForceSslStream_IgnoresProbeState()
        {
            TlsProviderSelector.MarkSslStreamBroken();
            // Explicit SslStream backend bypasses the probe — user asked for it explicitly
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }

        [Test]
        public void ResetProbeState_ClearsBrokenFlag()
        {
            TlsProviderSelector.MarkSslStreamBroken();
            Assert.IsTrue(TlsProviderSelector.IsSslStreamKnownBroken());
            TlsProviderSelector.ResetProbeState();
            Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken());
        }

        [Test]
        public void IsPlatformTlsException_ClassifiesCorrectly()
        {
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new PlatformNotSupportedException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new NotSupportedException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new TypeLoadException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new TypeInitializationException("T", null)));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new MissingMethodException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new EntryPointNotFoundException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new System.IO.FileNotFoundException()));
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(new DllNotFoundException()));

            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(
                new System.Security.Authentication.AuthenticationException()));
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(new System.IO.IOException()));
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(new OperationCanceledException()));
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(new InvalidOperationException()));
        }

        [Test]
        public void IsPlatformTlsException_UnwrapsTargetInvocationException()
        {
            var inner = new PlatformNotSupportedException("ALPN unavailable");
            var wrapped = new System.Reflection.TargetInvocationException(inner);
            Assert.IsTrue(TlsProviderSelector.IsPlatformTlsException(wrapped),
                "Should unwrap TargetInvocationException from reflection-based ALPN auth");

            var nonPlatformInner = new System.IO.IOException("network error");
            var wrappedNonPlatform = new System.Reflection.TargetInvocationException(nonPlatformInner);
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(wrappedNonPlatform),
                "Should not treat wrapped IOException as platform exception");
        }

        // --- DiagnosticLogger tests ---

        [SetUp]
        public void ClearDiagnosticLogger()
        {
            TlsProviderSelector.DiagnosticLogger = null;
        }

        [Test]
        public void DiagnosticLogger_DefaultIsNull()
        {
            // Logger is null by default; no message is emitted.
            Assert.IsNull(TlsProviderSelector.DiagnosticLogger);
        }

        [Test]
        public void DiagnosticLogger_MarkSslStreamViable_LogsOnFirstTransition()
        {
            TlsProviderSelector.ResetProbeState(); // ensure initial state = 0 regardless of prior tests
            var messages = new System.Collections.Generic.List<string>();
            TlsProviderSelector.DiagnosticLogger = messages.Add;

            TlsProviderSelector.MarkSslStreamViable(); // first transition 0→1 should log
            Assert.AreEqual(1, messages.Count, "Should log exactly once on first transition");
            StringAssert.Contains("SslStream", messages[0]);

            TlsProviderSelector.MarkSslStreamViable(); // already 1, no second log
            Assert.AreEqual(1, messages.Count, "Should not log again if already viable");
        }

        [Test]
        public void DiagnosticLogger_MarkSslStreamBroken_LogsOnFirstTransition()
        {
            TlsProviderSelector.ResetProbeState(); // ensure state starts at 0
            var messages = new System.Collections.Generic.List<string>();
            TlsProviderSelector.DiagnosticLogger = messages.Add;

            TlsProviderSelector.MarkSslStreamBroken(); // first transition to broken should log
            Assert.AreEqual(1, messages.Count, "Should log on first broken transition");
            StringAssert.Contains("BouncyCastle", messages[0]);

            TlsProviderSelector.MarkSslStreamBroken(); // already 2, no second log
            Assert.AreEqual(1, messages.Count, "Should not log again if already broken");
        }

        [Test]
        public void DiagnosticLogger_NullLogger_DoesNotThrow()
        {
            TlsProviderSelector.DiagnosticLogger = null;
            // These should not throw even when logger is null.
            Assert.DoesNotThrow(() => TlsProviderSelector.MarkSslStreamViable());
            Assert.DoesNotThrow(() => TlsProviderSelector.MarkSslStreamBroken());
        }

        [Test]
        public void DiagnosticLogger_CanBeChangedAtRuntime()
        {
            TlsProviderSelector.ResetProbeState(); // ensure state starts at 0
            var firstLog = new System.Collections.Generic.List<string>();
            TlsProviderSelector.DiagnosticLogger = firstLog.Add;
            TlsProviderSelector.MarkSslStreamViable();
            Assert.AreEqual(1, firstLog.Count);

            TlsProviderSelector.DiagnosticLogger = null;
            TlsProviderSelector.MarkSslStreamBroken(); // logger is null — should be silent
            Assert.AreEqual(1, firstLog.Count, "No new messages when logger is null");
        }
    }
}
