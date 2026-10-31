using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// The default transports must be registered by Unity's own startup hook, not by the
    /// <c>[ModuleInitializer]</c> Unity 2021.3 does not run reliably, and not by a manual
    /// <c>EnsureRegistered()</c> call (#1).
    /// </summary>
    [TestFixture]
    public class TransportBootstrapTests
    {
        private static readonly MethodInfo Bootstrap = typeof(TurboHttpBootstrap).GetMethod(
            "RegisterDefaultTransports", BindingFlags.Static | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            WebSocketTransportFactory.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            // Leave the registration play-mode entry produced.
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
            WebSocketTransportFactory.Reset();
            TurboHttpBootstrap.RegisterDefaultTransports();
        }

        [Test]
        public void Bootstrap_RunsAtSubsystemRegistration_AndSurvivesStripping()
        {
            Assert.That(Bootstrap, Is.Not.Null, "TurboHttpBootstrap.RegisterDefaultTransports");

            var hook = Bootstrap.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(hook, Is.Not.Null, "[RuntimeInitializeOnLoadMethod] on the bootstrap");
            Assert.That(hook.loadType, Is.EqualTo(RuntimeInitializeLoadType.SubsystemRegistration));
            Assert.That(Bootstrap.GetCustomAttribute<PreserveAttribute>(), Is.Not.Null, "[Preserve] on the method");
            Assert.That(typeof(TurboHttpBootstrap).GetCustomAttribute<PreserveAttribute>(), Is.Not.Null,
                "[Preserve] on the type");
            Assert.That(typeof(TurboHttpBootstrap).Assembly.GetCustomAttribute<AlwaysLinkAssemblyAttribute>(),
                Is.Not.Null, "[assembly: AlwaysLinkAssembly] so UnityLinker processes the unreferenced assembly");
#if UNITY_EDITOR
            Assert.That(Bootstrap.GetCustomAttribute<UnityEditor.InitializeOnLoadMethodAttribute>(), Is.Not.Null,
                "[InitializeOnLoadMethod] so edit-mode code gets the defaults too");
#endif
        }

        [Test]
        public void Bootstrap_RegistersTheDefaultHttpTransport()
        {
            Assert.Throws<InvalidOperationException>(() => _ = HttpTransportFactory.Default,
                "precondition: no transport registered after Reset");

            TurboHttpBootstrap.RegisterDefaultTransports();

            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());
            using (var backend = HttpTransportFactory.CreateWithBackend(TlsBackend.SslStream))
                Assert.That(backend, Is.InstanceOf<RawSocketTransport>());
            using (var client = new UHttpClient())
                Assert.That(client, Is.Not.Null);
        }

        [Test]
        public void Bootstrap_DisposesThePreviousSessionsDefaultTransport()
        {
            // Enter Play Mode without a domain reload: the previous session registered a transport
            // and Default created it; the bootstrap runs again at the next play-mode entry.
            var previous = new DisposeCountingTransport();
            HttpTransportFactory.Register(() => previous);
            Assert.That(HttpTransportFactory.Default, Is.SameAs(previous), "precondition");

            TurboHttpBootstrap.RegisterDefaultTransports();

            Assert.That(previous.DisposeCount, Is.EqualTo(1), "previous session's transport disposed once");
            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());

            TurboHttpBootstrap.RegisterDefaultTransports();
            Assert.That(previous.DisposeCount, Is.EqualTo(1), "never disposed twice");
        }

        [Test]
        public void Bootstrap_DoesNotCreateAnUnusedPreviousDefault()
        {
            var created = 0;
            HttpTransportFactory.Register(() =>
            {
                created++;
                return new DisposeCountingTransport();
            });

            TurboHttpBootstrap.RegisterDefaultTransports();

            Assert.That(created, Is.Zero, "a default nobody read is not created just to be disposed");
            Assert.That(HttpTransportFactory.Default, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void Bootstrap_RegistersTheDefaultWebSocketTransport()
        {
            Assert.Throws<InvalidOperationException>(() => WebSocketTransportFactory.Create(),
                "precondition: no WebSocket transport registered after Reset");

            TurboHttpBootstrap.RegisterDefaultTransports();

            var transport = WebSocketTransportFactory.Create();
            try
            {
                Assert.That(transport, Is.InstanceOf<RawSocketWebSocketTransport>());
            }
            finally
            {
                transport.Dispose();
            }
        }

        private sealed class DisposeCountingTransport : IHttpTransport
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
