using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// A client built with default options shares <see cref="HttpTransportFactory.Default"/>, and
    /// registering <see cref="RawSocketTransport"/> again never drops the default it already
    /// created (#275).
    /// </summary>
    [TestFixture]
    public class DefaultTransportRegistrationTests
    {
        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            // Leave the registration play-mode entry produced; later fixtures (ConnectionOptionsTests
            // has no SetUp) depend on it.
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
            TurboHttpBootstrap.RegisterDefaultTransports();
        }

        [Test]
        public void FreshOptions_AreDefault()
        {
            Assert.That(new Http2Options().IsDefault(), Is.True, "Http2Options");
            Assert.That(new ConnectionPoolOptions().IsDefault(), Is.True, "ConnectionPoolOptions");
            Assert.That(new StreamingOptions().IsDefault(), Is.True, "StreamingOptions");

            var options = new UHttpClientOptions();
            Assert.That(options.Http2.IsDefault(), Is.True, "UHttpClientOptions.Http2");
            Assert.That(options.ConnectionPool.IsDefault(), Is.True, "UHttpClientOptions.ConnectionPool");
            Assert.That(options.Streaming.IsDefault(), Is.True, "UHttpClientOptions.Streaming");
        }

        [Test]
        public void Http2Options_WithAResponseBodyCap_AreNotDefault()
        {
            var options = new Http2Options { MaxResponseBodySize = 100 * 1024 * 1024 };
            Assert.That(options.IsDefault(), Is.False);
        }

        [Test]
        public void DefaultOptionsClient_UsesTheRegisteredDefault_AndNeverTheOptionFactories()
        {
            var shared = new CountingTransport();
            HttpTransportFactory.Register(
                () => shared,
                _ => throw new AssertionException("backend factory used for default options"),
                (Func<TlsBackend, ConnectionPoolOptions, Http2Options, StreamingOptions, IHttpTransport>)
                ((_, _, _, _) => throw new AssertionException("options factory used for default options")));

            using (var client = new UHttpClient(new UHttpClientOptions()))
                Assert.That(client.Transport, Is.SameAs(shared));

            Assert.That(shared.DisposeCount, Is.Zero, "a client never disposes the shared default");
        }

        [Test]
        public void EnsureRegistered_Again_KeepsTheCreatedDefault()
        {
            RawSocketTransport.EnsureRegistered();
            var created = HttpTransportFactory.Default;
            var capabilities = HttpTransportFactory.Capabilities;

            RawSocketTransport.EnsureRegistered();

            Assert.That(HttpTransportFactory.Default, Is.SameAs(created));
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(capabilities), "capabilities stay registered");
        }

        [Test]
        public void EnsureRegistered_Again_KeepsAnEarlierClientOnTheSharedDefault()
        {
            RawSocketTransport.EnsureRegistered();
            using var client = new UHttpClient(new UHttpClientOptions());

            RawSocketTransport.EnsureRegistered();

            Assert.That(client.Transport, Is.SameAs(HttpTransportFactory.Default));
        }

        [Test]
        public void EnsureRegistered_ReplacesAnotherRegistration()
        {
            var other = new CountingTransport();
            HttpTransportFactory.Register(() => other);
            Assert.That(HttpTransportFactory.Default, Is.SameAs(other), "precondition");

            RawSocketTransport.EnsureRegistered();

            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void EnsureRegistered_ReplacesATestOverride()
        {
            RawSocketTransport.EnsureRegistered();
            var fake = new CountingTransport();
            HttpTransportFactory.SetForTesting(fake);
            Assert.That(HttpTransportFactory.Default, Is.SameAs(fake), "precondition");

            RawSocketTransport.EnsureRegistered();

            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void EnsureRegistered_AfterReset_RegistersAgain()
        {
            RawSocketTransport.EnsureRegistered();
            _ = HttpTransportFactory.Default;
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
            Assert.Throws<InvalidOperationException>(() => _ = HttpTransportFactory.Default, "precondition");

            RawSocketTransport.EnsureRegistered();

            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void Bootstrap_StillReplacesTheCreatedRawSocketDefault()
        {
            // Enter Play Mode without a domain reload: the bootstrap detaches (and disposes) the
            // previous session's RawSocketTransport and the next read creates a fresh one.
            RawSocketTransport.EnsureRegistered();
            var previous = HttpTransportFactory.Default;

            TurboHttpBootstrap.RegisterDefaultTransports();

            var next = HttpTransportFactory.Default;
            Assert.That(next, Is.InstanceOf<RawSocketTransport>());
            Assert.That(next, Is.Not.SameAs(previous));
        }

        private sealed class CountingTransport : IHttpTransport
        {
            public int DisposeCount;

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("not dispatched in this test");
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
