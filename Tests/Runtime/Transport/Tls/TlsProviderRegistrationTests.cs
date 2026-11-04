using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// The BouncyCastle provider reaches <see cref="TlsProviderSelector"/> by registration, not by a
    /// by-name type lookup (#186), and BouncyCastle is opt-in through TURBOHTTP_BOUNCYCASTLE (#196).
    /// The glue registers itself at startup in the test build, so each test clears the slot and
    /// restores the registered provider afterwards.
    /// </summary>
    [TestFixture]
    public class TlsProviderRegistrationTests
    {
        private ITlsProvider _registered;

        [SetUp]
        public void SetUp()
        {
            _registered = TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(null);
        }

        [TearDown]
        public void TearDown()
        {
            TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(_registered);
        }

        [Test]
        public void NothingRegistered_BouncyCastleIsUnavailable_AndExplicitSelectionNamesTheOptInDefine()
        {
            Assert.That(TlsProviderSelector.IsBouncyCastleAvailable(), Is.False);

            var ex = Assert.Throws<InvalidOperationException>(
                () => TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
            StringAssert.Contains("TURBOHTTP_BOUNCYCASTLE", ex.Message);
            StringAssert.Contains("BouncyCastleModuleRegistration.EnsureRegistered", ex.Message);
        }

        [Test]
        public void RegisteredProvider_IsReturnedForExplicitBouncyCastle_WithoutTheBouncyCastleAssembly()
        {
            var stub = new StubTlsProvider();
            TlsProviderSelector.RegisterBouncyCastleProvider(stub);

            Assert.That(TlsProviderSelector.IsBouncyCastleAvailable(), Is.True);
            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle), Is.SameAs(stub));
            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle), Is.SameAs(stub));
        }

        [Test]
        public void Register_IsIdempotent_AndRejectsNull()
        {
            var stub = new StubTlsProvider();
            TlsProviderSelector.RegisterBouncyCastleProvider(stub);
            TlsProviderSelector.RegisterBouncyCastleProvider(stub);

            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle), Is.SameAs(stub));
            Assert.Throws<ArgumentNullException>(() => TlsProviderSelector.RegisterBouncyCastleProvider(null));
            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle), Is.SameAs(stub));
        }

        [Test]
        public void RegisteredProvider_IsNeverSelectedByAuto()
        {
            var stub = new StubTlsProvider();
            TlsProviderSelector.RegisterBouncyCastleProvider(stub);

            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.Auto), Is.Not.SameAs(stub));
            Assert.That(TlsProviderSelector.GetProvider(TlsBackend.SslStream), Is.Not.SameAs(stub));
        }

        [Test]
        public void LateRegistration_RefreshesTheCachedCapabilitySummary()
        {
            Assert.That(TlsPlatformCapabilities.GetSummary().IsBouncyCastleAvailable, Is.False);

            TlsProviderSelector.RegisterBouncyCastleProvider(new StubTlsProvider());

            Assert.That(TlsPlatformCapabilities.GetSummary().IsBouncyCastleAvailable, Is.True);
        }

        private sealed class StubTlsProvider : ITlsProvider
        {
            public string ProviderName => "Stub";

            public Task<TlsResult> WrapAsync(Stream innerStream, string host, string[] alpnProtocols,
                CancellationToken ct) => throw new NotSupportedException();

            public bool IsAlpnSupported() => false;
        }
    }
}
