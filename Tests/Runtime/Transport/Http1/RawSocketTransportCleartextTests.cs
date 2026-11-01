using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #85: the HTTP transport shares AllowCleartext with the WebSocket transport. http:// targets
    /// and http:// proxy legs are refused before any socket unless it is set, and CONNECT sends
    /// Basic proxy credentials only when the 407 offers Basic.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportCleartextTests
    {
        [Test]
        public void HttpTarget_AllowCleartextFalse_ThrowsInvalidRequestBeforeAnySocket()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    AllowCleartext = false,
                    DefaultTimeout = TimeSpan.FromSeconds(2)
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("http://127.0.0.1:" + port + "/plain").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                StringAssert.Contains("AllowCleartext", ex.HttpError.Message);
                Assert.IsFalse(listener.Pending(), "no connection may be opened");
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void HttpsTargetThroughHttpProxy_AllowCleartextFalse_RefusesProxyBeforeAnySocket()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    AllowCleartext = false,
                    DefaultTimeout = TimeSpan.FromSeconds(2),
                    Proxy = new ProxySettings
                    {
                        Address = new Uri("http://127.0.0.1:" + port),
                        Credentials = new NetworkCredential("user", "pass"),
                        AllowPlaintextProxyAuth = true,
                        UseEnvironmentVariables = false
                    }
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                StringAssert.Contains("proxy", ex.HttpError.Message);
                Assert.IsFalse(listener.Pending(), "the proxy must never be contacted");
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void HttpTarget_PlatformPolicyRefusingHost_ThrowsEvenWithAllowCleartext()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var previous = CleartextTrafficPolicy.PlatformPolicy;
            CleartextTrafficPolicy.PlatformPolicy = host => host != "127.0.0.1";
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    AllowCleartext = true,
                    DefaultTimeout = TimeSpan.FromSeconds(2)
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("http://127.0.0.1:" + port + "/plain").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                Assert.IsFalse(listener.Pending(), "no connection may be opened");
            }
            finally
            {
                CleartextTrafficPolicy.PlatformPolicy = previous;
                listener.Stop();
            }
        }

        [Test]
        public void Options_DefaultFollowsPlatformAndCloneCopiesAllowCleartext()
        {
            var options = new UHttpClientOptions();
            Assert.AreEqual(CleartextTrafficPolicy.DefaultAllowCleartext, options.AllowCleartext);

            options.AllowCleartext = !options.AllowCleartext;
            Assert.AreEqual(options.AllowCleartext, options.Clone().AllowCleartext);
        }

        [TestCase("Digest realm=\"proxy\", nonce=\"abc\"")]
        [TestCase("Negotiate")]
        public void Connect407WithoutBasicChallenge_DoesNotSendCredentials(string challenge)
        {
            using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);
            var stream = new ScriptedProxyStream(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Proxy-Authenticate: " + challenge + "\r\n" +
                "Content-Length: 0\r\n\r\n" +
                "HTTP/1.1 200 Connection Established\r\n\r\n");
            var proxy = new ProxySettings
            {
                Credentials = new NetworkCredential("user", "pass"),
                AllowPlaintextProxyAuth = true
            };

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => InvokeEstablishConnectTunnelAsync(transport, stream, "api.example.com", 443, proxy));

            Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
            StringAssert.Contains("Basic", ex.HttpError.Message);
            Assert.IsFalse(stream.Written.Contains("Proxy-Authorization"), stream.Written);
            Assert.AreEqual(1, stream.Written.Split(new[] { "CONNECT " }, StringSplitOptions.None).Length - 1);
        }

        private static Task InvokeEstablishConnectTunnelAsync(
            RawSocketTransport transport,
            Stream stream,
            string targetHost,
            int targetPort,
            ProxySettings proxy)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "EstablishConnectTunnelAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method);

            return (Task)method.Invoke(
                transport,
                new object[] { stream, targetHost, targetPort, proxy, false, CancellationToken.None });
        }

        /// <summary>Serves a scripted proxy reply and records what the client wrote.</summary>
        private sealed class ScriptedProxyStream : Stream
        {
            private readonly MemoryStream _script;
            private readonly MemoryStream _written = new MemoryStream();

            public ScriptedProxyStream(string script)
            {
                _script = new MemoryStream(Encoding.ASCII.GetBytes(script), writable: false);
            }

            public string Written => Encoding.ASCII.GetString(_written.ToArray());

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _script.Read(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
