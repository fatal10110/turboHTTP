using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Platform
{
    /// <summary>
    /// <see cref="PlatformConfig"/> holds no reflection-initialized static state and makes no
    /// UnityEngine call on the option-construction path; HTTP/2 capability comes from transport
    /// registration on <see cref="HttpTransportFactory"/> (#179, #186).
    /// </summary>
    [TestFixture]
    public class PlatformConfigNoReflectionTests
    {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        private const int ExpectedTimeoutSeconds = 45;
        private const int ExpectedConcurrency = 8;
#else
        private const int ExpectedTimeoutSeconds = 30;
        private const int ExpectedConcurrency = 16;
#endif

        [TearDown]
        public void TearDown()
        {
            // Leave the registration play-mode entry produced.
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
            TurboHttpBootstrap.RegisterDefaultTransports();
        }

        [Test]
        public void PlatformConfig_HasNoStaticInitializer_AndNoStaticFields()
        {
            var type = typeof(PlatformConfig);
            Assert.That(type.TypeInitializer, Is.Null,
                "PlatformConfig must not run a static constructor (it used to reflect into Transport).");

            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                Assert.That(field.IsLiteral, Is.True, "Only compile-time constants allowed: " + field.Name);
            }
        }

        [Test]
        public void Options_ConstructedOnBackgroundThread_UseBuildTargetDefaults()
        {
            var testThread = Thread.CurrentThread.ManagedThreadId;
            AssertAsync.Run(() => Task.Run(() =>
            {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.Not.EqualTo(testThread));

                var options = new UHttpClientOptions();
                var pool = new ConnectionPoolOptions();

                Assert.That(options.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(ExpectedTimeoutSeconds)));
                Assert.That(pool.MaxConnectionsPerHost, Is.EqualTo(ExpectedConcurrency));
                Assert.That(PlatformConfig.RecommendedTimeout, Is.EqualTo(TimeSpan.FromSeconds(ExpectedTimeoutSeconds)));
                Assert.That(PlatformConfig.RecommendedMaxConcurrency, Is.EqualTo(ExpectedConcurrency));
            }));
        }

        [Test]
        public void SupportsHttp2_ComesFromTransportCapabilityRegistration()
        {
            HttpTransportFactory.Reset();
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(TransportCapabilities.None));
            Assert.That(PlatformConfig.SupportsHttp2, Is.False);

            HttpTransportFactory.RegisterCapabilities(() => TransportCapabilities.Http2);
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(TransportCapabilities.Http2));
            Assert.That(PlatformConfig.SupportsHttp2, Is.True);

            HttpTransportFactory.RegisterCapabilities(null);
            Assert.That(PlatformConfig.SupportsHttp2, Is.False);

            // Capabilities belong to the registered transport: a new registration clears them.
            HttpTransportFactory.RegisterCapabilities(() => TransportCapabilities.Http2);
            HttpTransportFactory.Register(() => new MockTransport());
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(TransportCapabilities.None));

            HttpTransportFactory.RegisterCapabilities(() => TransportCapabilities.Http2);
            HttpTransportFactory.Reset();
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(TransportCapabilities.None));
        }

        [Test]
        public void RawSocketTransport_EnsureRegistered_ReportsHttp2WhenAlpnIsAvailable()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();

            var expected = SslStreamTlsProvider.Instance.IsAlpnSupported() || TlsProviderSelector.IsBouncyCastleAvailable()
                ? TransportCapabilities.Http2
                : TransportCapabilities.None;
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(expected));
            Assert.That(PlatformConfig.SupportsHttp2, Is.EqualTo(expected == TransportCapabilities.Http2));
        }
    }
}
