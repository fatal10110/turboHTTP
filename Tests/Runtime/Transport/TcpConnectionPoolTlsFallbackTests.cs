using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Tls;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// TLS fallback rule (#34, #110): Auto mode picks the TLS provider before the handshake and
    /// never picks one without full certificate chain validation; a failed handshake (certificate,
    /// authentication, network or platform capability) propagates to the caller with no runtime
    /// catch-and-retry on another provider.
    /// </summary>
    [TestFixture]
    public class TcpConnectionPoolTlsFallbackTests
    {
        private sealed class ThrowingTlsProvider : ITlsProvider
        {
            private readonly Exception _failure;
            private int _wrapCalls;

            public ThrowingTlsProvider(Exception failure)
            {
                _failure = failure;
            }

            public int WrapCalls => Volatile.Read(ref _wrapCalls);

            public string ProviderName => "SslStream";

            public bool IsAlpnSupported() => true;

            public Task<TlsResult> WrapAsync(
                Stream innerStream, string host, string[] alpnProtocols, CancellationToken ct)
            {
                Interlocked.Increment(ref _wrapCalls);
                return Task.FromException<TlsResult>(_failure);
            }
        }

        [SetUp]
        [TearDown]
        public void ResetProbeState()
        {
            TlsProviderSelector.ResetProbeState();
        }

        [Test]
        public void Auto_CertificateFailure_PropagatesWithoutProviderSwitch()
        {
            using var server = LocalTlsTestServer.TryStart();
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("Local self-signed certificate generation is unavailable in this runtime.");
                return;
            }

            var requested = new ConcurrentQueue<TlsBackend>();
            var failure = CaptureFailure(async () =>
            {
                using var pool = new TcpConnectionPool(backend =>
                {
                    requested.Enqueue(backend);
                    return TlsProviderSelector.GetProvider(backend);
                });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var lease = await pool.GetConnectionAsync("127.0.0.1", server.Port, true, cts.Token);
            });

            Assert.IsNotNull(failure, "The self-signed certificate must be rejected.");
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(failure),
                "Expected a certificate/authentication failure, got " + failure.GetType().Name);
            CollectionAssert.AreEqual(new[] { TlsBackend.Auto }, requested.ToArray(),
                "The provider is resolved once, before the handshake; never re-resolved for a retry.");
            Assert.IsFalse(server.HasPendingConnection, "No second connection may be opened for a retry.");
            Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken(),
                "A certificate failure is not a platform failure.");
            Assert.AreEqual("SslStream", TlsProviderSelector.GetProvider(TlsBackend.Auto).ProviderName);
        }

        [Test]
        public void Auto_PlatformTlsException_PropagatesWithoutProviderSwitch()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var provider = new ThrowingTlsProvider(
                    new PlatformNotSupportedException("simulated SslStream platform gap"));
                var requested = new ConcurrentQueue<TlsBackend>();

                var failure = CaptureFailure(async () =>
                {
                    using var pool = new TcpConnectionPool(backend =>
                    {
                        requested.Enqueue(backend);
                        return provider;
                    });
                    // Bounds the pre-fix behaviour (a BouncyCastle retry on an unanswered socket).
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var lease = await pool.GetConnectionAsync("127.0.0.1", port, true, cts.Token);
                });

                Assert.IsInstanceOf<PlatformNotSupportedException>(failure,
                    "The platform exception must reach the caller unchanged.");
                Assert.AreEqual(1, provider.WrapCalls);
                CollectionAssert.AreEqual(new[] { TlsBackend.Auto }, requested.ToArray());

                // Exactly one TCP connection: the connect completed before WrapAsync, so it sits in
                // the backlog; a reconnect for a provider retry would add a second one.
                Assert.IsTrue(listener.Pending(), "The original connection should be in the backlog.");
                listener.AcceptTcpClient().Close();
                Assert.IsFalse(listener.Pending(), "No second connection may be opened for a retry.");

                Assert.IsTrue(TlsProviderSelector.IsSslStreamKnownBroken(),
                    "The platform failure is recorded for diagnostics.");
                Assert.AreEqual("SslStream", TlsProviderSelector.GetProvider(TlsBackend.Auto).ProviderName,
                    "A recorded platform failure never routes Auto to another provider.");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static Exception CaptureFailure(Func<Task> action)
        {
            try
            {
                Task.Run(action).GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
