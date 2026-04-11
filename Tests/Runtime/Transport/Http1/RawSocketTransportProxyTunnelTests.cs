using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    [TestFixture]
    public class RawSocketTransportProxyTunnelTests
    {
        private sealed class ConnectProxyServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _gate = new object();
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public ConnectProxyServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        continue;
                    }

                    lock (_gate)
                    {
                        _clients.Add(client);
                    }

                    Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }

            private static async Task HandleClientAsync(TcpClient client)
            {
                TcpClient originClient = null;
                try
                {
                    using var clientStream = client.GetStream();
                    var request = await ReadRequestStartAndHeadersAsync(clientStream, CancellationToken.None);
                    var authority = ParseConnectAuthority(request.RequestLine);

                    originClient = new TcpClient();
                    await originClient.ConnectAsync(authority.host, authority.port);

                    using var originStream = originClient.GetStream();
                    await WriteResponseAsync(
                        clientStream,
                        "HTTP/1.1 200 Connection Established\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                    await RelayTunnelAsync(clientStream, originStream).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    try { originClient?.Close(); } catch { }
                    try { client.Close(); } catch { }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                lock (_gate)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }

                    _clients.Clear();
                }
            }
        }

        private sealed class HttpsOriginServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _gate = new object();
            private readonly Func<SslStream, int, Task> _handler;
            private readonly X509Certificate2 _certificate;
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public HttpsOriginServer(X509Certificate2 certificate, Func<SslStream, int, Task> handler)
            {
                _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        continue;
                    }

                    lock (_gate)
                    {
                        _clients.Add(client);
                    }

                    var index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleClientAsync(client, index));
                }
            }

            private async Task HandleClientAsync(TcpClient client, int index)
            {
                try
                {
                    using var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await sslStream.AuthenticateAsServerAsync(
                        _certificate,
                        clientCertificateRequired: false,
                        enabledSslProtocols: SslProtocols.Tls12,
                        checkCertificateRevocation: false);
                    await _handler(sslStream, index).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                lock (_gate)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }

                    _clients.Clear();
                }
            }
        }

        private sealed class FailingStream : Stream
        {
            private readonly Stream _inner;

            public FailingStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken)
                => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
                => throw new IOException("Injected failure");

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromException<int>(new IOException("Injected failure"));

            public override void Write(byte[] buffer, int offset, int count)
                => throw new IOException("Injected failure");

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromException(new IOException("Injected failure"));

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }

        private sealed class ConnectThenFailTlsStream : Stream
        {
            private readonly byte[] _connectResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 Connection Established\r\nContent-Length: 0\r\n\r\n");
            private readonly StringBuilder _connectRequest = new StringBuilder();
            private int _responseOffset;
            private bool _connectResponseFullyRead;

            public string ConnectRequest => _connectRequest.ToString();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_responseOffset >= _connectResponse.Length)
                {
                    _connectResponseFullyRead = true;
                    return 0;
                }

                var available = _connectResponse.Length - _responseOffset;
                var toCopy = Math.Min(available, count);
                Buffer.BlockCopy(_connectResponse, _responseOffset, buffer, offset, toCopy);
                _responseOffset += toCopy;
                if (_responseOffset >= _connectResponse.Length)
                    _connectResponseFullyRead = true;

                return toCopy;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_connectResponseFullyRead)
                    throw new IOException("Injected TLS write failure.");

                _connectRequest.Append(Encoding.ASCII.GetString(buffer, offset, count));
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class ConnectAuthChallengeThenFailTlsStream : Stream
        {
            private static readonly byte[] AuthRequiredResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Proxy-Authenticate: Basic realm=\"test\"\r\n" +
                "Content-Length: 0\r\n\r\n");
            private static readonly byte[] ConnectEstablishedResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 Connection Established\r\nContent-Length: 0\r\n\r\n");

            private readonly List<string> _connectRequests = new List<string>();
            private readonly StringBuilder _pendingRequest = new StringBuilder();
            private byte[] _currentResponse;
            private int _responseOffset;
            private bool _connectEstablished;

            public IReadOnlyList<string> ConnectRequests => _connectRequests;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_currentResponse == null || _responseOffset >= _currentResponse.Length)
                    return 0;

                var available = _currentResponse.Length - _responseOffset;
                var toCopy = Math.Min(available, count);
                Buffer.BlockCopy(_currentResponse, _responseOffset, buffer, offset, toCopy);
                _responseOffset += toCopy;
                if (_responseOffset >= _currentResponse.Length &&
                    _connectRequests.Count >= 2)
                {
                    _connectEstablished = true;
                }

                return toCopy;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_connectEstablished)
                    throw new IOException("Injected TLS write failure.");

                _pendingRequest.Append(Encoding.ASCII.GetString(buffer, offset, count));
                if (_pendingRequest.ToString().Contains("\r\n\r\n"))
                {
                    _connectRequests.Add(_pendingRequest.ToString());
                    _pendingRequest.Length = 0;
                    _currentResponse = _connectRequests.Count == 1
                        ? AuthRequiredResponse
                        : ConnectEstablishedResponse;
                    _responseOffset = 0;
                }
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class SocketPair : IDisposable
        {
            private readonly TcpClient _client;
            private readonly TcpClient _server;

            public Socket ClientSocket => _client.Client;
            public int Port { get; }

            private SocketPair(TcpClient client, TcpClient server, int port)
            {
                _client = client;
                _server = server;
                Port = port;
            }

            public static async Task<SocketPair> CreateAsync()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var acceptTask = listener.AcceptTcpClientAsync();
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    var server = await acceptTask;
                    listener.Stop();
                    return new SocketPair(client, server, port);
                }
                catch
                {
                    listener.Stop();
                    try { client.Close(); } catch { }
                    throw;
                }
            }

            public void Dispose()
            {
                try { _client.Close(); } catch { }
                try { _server.Close(); } catch { }
            }
        }

        [Test]
        public void ConnectTunnelAlpnProtocols_AdvertisesH2BeforeHttp11()
        {
            var field = typeof(RawSocketTransport).GetField(
                "s_connectTunnelAlpnProtocols",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);

            var protocols = (string[])field.GetValue(null);
            CollectionAssert.AreEqual(new[] { "h2", "http/1.1" }, protocols);
        }

        [TestCase("h2")]
        [TestCase("http/1.1")]
        [TestCase(null)]
        public void RebindConnectTunnelLease_StoresNegotiatedAlpn(string negotiatedAlpn)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var originalStream = new MemoryStream();
            using var secureStream = new MemoryStream();
            using var semaphore = new SemaphoreSlim(0, 1);

            var connection = new PooledConnection(
                socket,
                originalStream,
                "127.0.0.1",
                8080,
                false);
            using var lease = new ConnectionLease(null, semaphore, connection);
            var tlsResult = new TlsResult(
                secureStream,
                negotiatedAlpn,
                "1.3",
                providerName: "SslStream");

            InvokeRebindConnectTunnelLease(
                lease,
                "api.example.com",
                443,
                "tunnel|proxy|origin",
                tlsResult);

            Assert.AreSame(secureStream, lease.Connection.Stream);
            Assert.AreEqual("api.example.com", lease.Connection.Host);
            Assert.AreEqual(443, lease.Connection.Port);
            Assert.IsTrue(lease.Connection.IsSecure);
            Assert.AreEqual("SslStream", lease.Connection.TlsProviderName);
            Assert.AreEqual("1.3", lease.Connection.TlsVersion);
            Assert.AreEqual(negotiatedAlpn, lease.Connection.NegotiatedAlpnProtocol);
        }

        [Test]
        public void MarkSslStreamViableIfAuto_SslStreamProvider_SetsViableProbeState()
        {
            TlsProviderSelector.ResetProbeState();
            try
            {
                using var stream = new MemoryStream();
                var tlsResult = new TlsResult(stream, "h2", "1.3", providerName: "SslStream");

                InvokeMarkSslStreamViableIfAuto(TlsBackend.Auto, tlsResult);

                Assert.AreEqual(1, GetSslStreamViabilityState());
                Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken());
            }
            finally
            {
                TlsProviderSelector.ResetProbeState();
            }
        }

        [Test]
        public void MarkSslStreamViableIfAuto_BouncyCastleProvider_DoesNotPromoteSslStream()
        {
            TlsProviderSelector.ResetProbeState();
            try
            {
                using var stream = new MemoryStream();
                var tlsResult = new TlsResult(stream, "h2", "1.3", providerName: "BouncyCastle");

                InvokeMarkSslStreamViableIfAuto(TlsBackend.Auto, tlsResult);

                Assert.AreEqual(0, GetSslStreamViabilityState());
                Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken());
            }
            finally
            {
                TlsProviderSelector.ResetProbeState();
            }
        }

        [Test]
        public void EstablishConnectTunnelAsync_TlsFailure_PropagatesProviderException()
        {
            AssertAsync.Run(async () =>
            {
                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                if (!provider.IsAlpnSupported())
                    Assert.Ignore("SslStream ALPN is unavailable in this runtime.");

                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);
                var stream = new ConnectThenFailTlsStream();

                Exception exception = null;
                try
                {
                    await InvokeEstablishConnectTunnelAsync(
                        transport,
                        stream,
                        "api.example.com",
                        443,
                        CancellationToken.None);
                    Assert.Fail("Expected TLS provider failure.");
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                Assert.IsNotNull(exception);
                Assert.IsFalse(exception is UHttpException);
                StringAssert.Contains("Injected TLS write failure.", exception.ToString());
                StringAssert.StartsWith("CONNECT api.example.com:443 HTTP/1.1", stream.ConnectRequest);
            });
        }

        [Test]
        public void PrepareHttpsProxyTunnelRequest_StripsProxyAuthorizationBeforeOriginDispatch()
        {
            var request = new UHttpRequest(
                HttpMethod.GET,
                new Uri("https://api.example.com/resource"))
                .WithHeader("Proxy-Authorization", "Basic should-not-leak")
                .WithHeader("X-Test", "kept");

            var tunneled = InvokePrepareHttpsProxyTunnelRequest(request);

            Assert.IsFalse(tunneled.Headers.Contains("Proxy-Authorization"));
            Assert.AreEqual("kept", tunneled.Headers.Get("X-Test"));
        }

        [Test]
        public void EstablishConnectTunnelAsync_ProxyAuthChallenge_SendsProxyAuthorizationOnlyToProxy()
        {
            AssertAsync.Run(async () =>
            {
                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                if (!provider.IsAlpnSupported())
                    Assert.Ignore("SslStream ALPN is unavailable in this runtime.");

                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);
                var stream = new ConnectAuthChallengeThenFailTlsStream();
                var proxy = new ProxySettings
                {
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = true
                };

                try
                {
                    await InvokeEstablishConnectTunnelAsync(
                        transport,
                        stream,
                        "api.example.com",
                        443,
                        proxy,
                        CancellationToken.None);
                    Assert.Fail("Expected TLS provider failure after CONNECT authentication.");
                }
                catch (Exception ex)
                {
                    Assert.IsFalse(ex is UHttpException);
                    StringAssert.Contains("Injected TLS write failure.", ex.ToString());
                }

                Assert.AreEqual(2, stream.ConnectRequests.Count);
                StringAssert.StartsWith(
                    "CONNECT api.example.com:443 HTTP/1.1",
                    stream.ConnectRequests[0]);
                StringAssert.DoesNotContain(
                    "Proxy-Authorization:",
                    stream.ConnectRequests[0]);
                StringAssert.Contains(
                    "Proxy-Authorization: Basic dXNlcjpwYXNz",
                    stream.ConnectRequests[1]);
            });
        }

        [Test]
        public void HttpsViaProxy_PreparedTunnelWithH2Alpn_UsesTunneledHttp2AndFastPathReuse()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var observedRequests = new List<List<(string Name, string Value)>>();
                var serverTask = RunHttp2OriginAsync(
                    duplex.ServerStream,
                    new[] { "one", "two" },
                    observedRequests,
                    cts.Token);

                using var socketPair = await SocketPair.CreateAsync();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, tlsBackend: TlsBackend.SslStream);
                using var transport = new RawSocketTransport(pool, TlsBackend.SslStream);

                var tunnelPoolKey = BuildTunnelPoolKey(socketPair.Port, "example.com", 443);
                var connection = new PooledConnection(
                    socketPair.ClientSocket,
                    duplex.ClientStream,
                    "127.0.0.1",
                    socketPair.Port,
                    false);
                connection.UpdateTransportBinding(
                    duplex.ClientStream,
                    "example.com",
                    443,
                    isSecure: true,
                    poolKey: tunnelPoolKey,
                    tlsVersion: "1.3",
                    tlsProviderName: "TestTls",
                    negotiatedAlpnProtocol: "h2");
                EnqueueIdleConnection(pool, tunnelPoolKey, connection);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{socketPair.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                using (var first = await client
                           .Get("https://example.com/h2-first")
                           .WithHeader("Proxy-Authorization", "Basic should-not-leak")
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                    Assert.AreEqual("one", first.GetBodyAsString());
                }

                Assert.IsTrue(transport.HasHttp2Connection(
                    "example.com",
                    443,
                    "127.0.0.1",
                    socketPair.Port));

                using (var second = await client
                           .Get("https://example.com/h2-second")
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                    Assert.AreEqual("two", second.GetBodyAsString());
                }

                await serverTask;

                Assert.AreEqual(2, observedRequests.Count);
                AssertHeader(observedRequests[0], ":path", "/h2-first");
                AssertHeader(observedRequests[1], ":path", "/h2-second");
                AssertNoHeader(observedRequests[0], "proxy-authorization");
            });
        }

        [Test]
        public void HttpsViaProxy_StaleHttp11RetryWithH2Alpn_ReroutesToHttp2()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var observedRequests = new List<List<(string Name, string Value)>>();
                var serverTask = RunHttp2OriginAsync(
                    duplex.ServerStream,
                    new[] { "rerouted" },
                    observedRequests,
                    cts.Token);

                using var staleSocketPair = await SocketPair.CreateAsync();
                using var h2SocketPair = await SocketPair.CreateAsync();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, tlsBackend: TlsBackend.SslStream);
                using var transport = new RawSocketTransport(pool, TlsBackend.SslStream);

                var tunnelPoolKey = BuildTunnelPoolKey(h2SocketPair.Port, "example.com", 443);
                var staleStream = new FailingStream(
                    new NetworkStream(staleSocketPair.ClientSocket, ownsSocket: false));
                var staleConnection = new PooledConnection(
                    staleSocketPair.ClientSocket,
                    staleStream,
                    "example.com",
                    443,
                    true);
                staleConnection.UpdateTransportBinding(
                    staleStream,
                    "example.com",
                    443,
                    isSecure: true,
                    poolKey: tunnelPoolKey,
                    tlsVersion: "1.3",
                    tlsProviderName: "TestTls",
                    negotiatedAlpnProtocol: "http/1.1");

                var h2Connection = new PooledConnection(
                    h2SocketPair.ClientSocket,
                    duplex.ClientStream,
                    "example.com",
                    443,
                    true);
                h2Connection.UpdateTransportBinding(
                    duplex.ClientStream,
                    "example.com",
                    443,
                    isSecure: true,
                    poolKey: tunnelPoolKey,
                    tlsVersion: "1.3",
                    tlsProviderName: "TestTls",
                    negotiatedAlpnProtocol: "h2");

                EnqueueIdleConnection(pool, tunnelPoolKey, staleConnection);
                EnqueueIdleConnection(pool, tunnelPoolKey, h2Connection);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{h2SocketPair.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                using (var response = await client
                           .Get("https://example.com/reroute")
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("rerouted", response.GetBodyAsString());
                }

                await serverTask;

                Assert.IsTrue(transport.HasHttp2Connection(
                    "example.com",
                    443,
                    "127.0.0.1",
                    h2SocketPair.Port));
                Assert.AreEqual(1, observedRequests.Count);
                AssertHeader(observedRequests[0], ":path", "/reroute");
            });
        }

        [Test]
        public void HttpsViaProxy_StreamingResponseDispose_ReusesConnectTunnel()
        {
            AssertAsync.Run(async () =>
            {
                if (!TlsProviderSelector.IsBouncyCastleAvailable())
                    Assert.Ignore("TurboHTTP.Transport.BouncyCastle is required for local CONNECT tunnel TLS tests.");

                using var certificate = TryCreateLocalhostCertificate();
                if (certificate == null)
                    Assert.Ignore("Local self-signed certificate generation is unavailable in this runtime.");
                string firstRequestLine = null;
                string secondRequestLine = null;

                using var originServer = new HttpsOriginServer(certificate, async (stream, _) =>
                {
                    var first = await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                    firstRequestLine = first.RequestLine;

                    await WriteResponseHeadAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n");
                    var hello = Encoding.ASCII.GetBytes("Hello");
                    await stream.WriteAsync(hello, 0, hello.Length);
                    await stream.FlushAsync();

                    var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                    secondRequestLine = followUp?.RequestLine;
                    if (followUp != null)
                    {
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    }
                });

                using var proxyServer = new ConnectProxyServer();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(tlsBackend: TlsBackend.BouncyCastle),
                    DisposeTransport = true,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                await using (var response = await client
                                 .Get($"https://localhost:{originServer.Port}/first")
                                 .SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    await response.DisposeAsync();
                }

                using var followUp = await client
                    .Get($"https://localhost:{originServer.Port}/second")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual("GET /first HTTP/1.1", firstRequestLine);
                Assert.AreEqual("GET /second HTTP/1.1", secondRequestLine);
                Assert.AreEqual(1, proxyServer.AcceptCount);
                Assert.AreEqual(1, originServer.AcceptCount);
            });
        }

        [Test]
        public void HttpsViaProxy_DifferentOrigins_OpenSeparateTunnels()
        {
            AssertAsync.Run(async () =>
            {
                if (!TlsProviderSelector.IsBouncyCastleAvailable())
                    Assert.Ignore("TurboHTTP.Transport.BouncyCastle is required for local CONNECT tunnel TLS tests.");

                using var certificate = TryCreateLocalhostCertificate();
                if (certificate == null)
                    Assert.Ignore("Local self-signed certificate generation is unavailable in this runtime.");

                using var originServerA = new HttpsOriginServer(certificate, async (stream, _) =>
                {
                    await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\na");
                });

                using var originServerB = new HttpsOriginServer(certificate, async (stream, _) =>
                {
                    await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nb");
                });

                using var proxyServer = new ConnectProxyServer();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(tlsBackend: TlsBackend.BouncyCastle),
                    DisposeTransport = true,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                using var responseA = await client
                    .Get($"https://localhost:{originServerA.Port}/alpha")
                    .SendBufferedAsync();
                using var responseB = await client
                    .Get($"https://localhost:{originServerB.Port}/beta")
                    .SendBufferedAsync();

                Assert.AreEqual("a", responseA.GetBodyAsString());
                Assert.AreEqual("b", responseB.GetBodyAsString());
                Assert.AreEqual(2, proxyServer.AcceptCount);
                Assert.AreEqual(1, originServerA.AcceptCount);
                Assert.AreEqual(1, originServerB.AcceptCount);
            });
        }

        [Test]
        public void HttpsViaProxy_StreamingResponseDisposeOverBudget_ClosesTunnel()
        {
            AssertAsync.Run(async () =>
            {
                if (!TlsProviderSelector.IsBouncyCastleAvailable())
                    Assert.Ignore("TurboHTTP.Transport.BouncyCastle is required for local CONNECT tunnel TLS tests.");

                using var certificate = TryCreateLocalhostCertificate();
                if (certificate == null)
                    Assert.Ignore("Local self-signed certificate generation is unavailable in this runtime.");

                string sameTunnelFollowUp = null;
                var largeBody = new byte[(64 * 1024) + 2];
                Array.Fill(largeBody, (byte)'a');

                using var originServer = new HttpsOriginServer(certificate, async (stream, index) =>
                {
                    if (index == 1)
                    {
                        try
                        {
                            await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                            await WriteResponseHeadAsync(
                                stream,
                                $"HTTP/1.1 200 OK\r\nContent-Length: {largeBody.Length}\r\nConnection: keep-alive\r\n\r\n");
                            await stream.WriteAsync(largeBody, 0, largeBody.Length);
                            await stream.FlushAsync();

                            var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromMilliseconds(300));
                            sameTunnelFollowUp = followUp?.RequestLine;
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }

                        return;
                    }

                    await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                });

                using var proxyServer = new ConnectProxyServer();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(tlsBackend: TlsBackend.BouncyCastle),
                    DisposeTransport = true,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                await using (var response = await client
                                 .Get($"https://localhost:{originServer.Port}/large")
                                 .SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    await response.DisposeAsync();
                }

                using var followUp = await client
                    .Get($"https://localhost:{originServer.Port}/second")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.IsNull(sameTunnelFollowUp);
                Assert.AreEqual(2, proxyServer.AcceptCount);
                Assert.AreEqual(2, originServer.AcceptCount);
            });
        }

        [Test]
        public void HttpsViaProxy_StaleTunnel_Idempotent_Retries()
        {
            AssertAsync.Run(async () =>
            {
                if (!TlsProviderSelector.IsBouncyCastleAvailable())
                    Assert.Ignore("TurboHTTP.Transport.BouncyCastle is required for local CONNECT tunnel TLS tests.");

                using var certificate = TryCreateLocalhostCertificate();
                if (certificate == null)
                    Assert.Ignore("Local self-signed certificate generation is unavailable in this runtime.");

                string requestLine = null;

                using var originServer = new HttpsOriginServer(certificate, async (stream, _) =>
                {
                    var request = await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                    requestLine = request.RequestLine;
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                });

                using var proxyServer = new ConnectProxyServer();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle);

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, proxyServer.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConnection = new PooledConnection(
                    failingClient.Client,
                    failingStream,
                    "127.0.0.1",
                    proxyServer.Port,
                    false);

                var tunnelPoolKey = BuildTunnelPoolKey(proxyServer.Port, "localhost", originServer.Port);
                failingConnection.UpdateTransportBinding(
                    failingStream,
                    "localhost",
                    originServer.Port,
                    isSecure: true,
                    poolKey: tunnelPoolKey,
                    tlsVersion: "1.3",
                    tlsProviderName: "TestTls",
                    negotiatedAlpnProtocol: "http/1.1");

                var idleField = typeof(TcpConnectionPool).GetField(
                    "_idleConnections",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var queue = idle.GetOrAdd(tunnelPoolKey, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConnection);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Proxy = new ProxySettings
                    {
                        Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                        UseEnvironmentVariables = false
                    }
                });

                using var response = await client
                    .Get($"https://localhost:{originServer.Port}/retry")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("GET /retry HTTP/1.1", requestLine);
                Assert.GreaterOrEqual(proxyServer.AcceptCount, 2);
                Assert.AreEqual(1, originServer.AcceptCount);
            });
        }

        private static Task RelayTunnelAsync(Stream clientStream, Stream originStream)
        {
            return Task.WhenAll(
                PumpAsync(clientStream, originStream),
                PumpAsync(originStream, clientStream));
        }

        private static async Task PumpAsync(Stream source, Stream destination)
        {
            try
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    await destination.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private static string BuildTunnelPoolKey(int proxyPort, string targetHost, int targetPort)
        {
            return "tunnel|" +
                TcpConnectionPool.BuildConnectionKey("127.0.0.1", proxyPort, secure: false) +
                "|" +
                TcpConnectionPool.BuildConnectionKey(targetHost, targetPort, secure: true);
        }

        private static (string host, int port) ParseConnectAuthority(string requestLine)
        {
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected CONNECT request line.");

            var authority = parts[1];
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || colon == authority.Length - 1)
                throw new InvalidOperationException("CONNECT request line is missing authority port.");

            return (authority.Substring(0, colon), int.Parse(authority.Substring(colon + 1)));
        }

        private static void InvokeRebindConnectTunnelLease(
            ConnectionLease lease,
            string targetHost,
            int targetPort,
            string tunnelPoolKey,
            TlsResult tlsResult)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "RebindConnectTunnelLease",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, new object[] { lease, targetHost, targetPort, tunnelPoolKey, tlsResult });
        }

        private static void EnqueueIdleConnection(
            TcpConnectionPool pool,
            string poolKey,
            PooledConnection connection)
        {
            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(idleField);

            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            var queue = idle.GetOrAdd(poolKey, _ => new ConcurrentQueue<PooledConnection>());
            queue.Enqueue(connection);
        }

        private static void InvokeMarkSslStreamViableIfAuto(TlsBackend tlsBackend, TlsResult tlsResult)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "MarkSslStreamViableIfAuto",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, new object[] { tlsBackend, tlsResult });
        }

        private static Task<TlsResult> InvokeEstablishConnectTunnelAsync(
            RawSocketTransport transport,
            Stream stream,
            string targetHost,
            int targetPort,
            CancellationToken ct)
        {
            return InvokeEstablishConnectTunnelAsync(
                transport,
                stream,
                targetHost,
                targetPort,
                null,
                ct);
        }

        private static Task<TlsResult> InvokeEstablishConnectTunnelAsync(
            RawSocketTransport transport,
            Stream stream,
            string targetHost,
            int targetPort,
            ProxySettings proxy,
            CancellationToken ct)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "EstablishConnectTunnelAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method);

            return (Task<TlsResult>)method.Invoke(
                transport,
                new object[] { stream, targetHost, targetPort, proxy, ct });
        }

        private static UHttpRequest InvokePrepareHttpsProxyTunnelRequest(UHttpRequest request)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "PrepareHttpsProxyTunnelRequest",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            return (UHttpRequest)method.Invoke(null, new object[] { request });
        }

        private static int GetSslStreamViabilityState()
        {
            var field = typeof(TlsProviderSelector).GetField(
                "_sslStreamViabilityState",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);

            return (int)field.GetValue(null);
        }

        private static async Task RunHttp2OriginAsync(
            Stream serverStream,
            string[] responseBodies,
            List<List<(string Name, string Value)>> observedRequests,
            CancellationToken ct)
        {
            var codec = await CompleteHttp2HandshakeAsync(serverStream, ct);
            var decoder = new HpackDecoder();

            for (int i = 0; i < responseBodies.Length; i++)
            {
                var requestHeaders = await ReadNextHeadersFrameAsync(codec, ct);
                observedRequests.Add(decoder.Decode(
                    requestHeaders.Payload,
                    0,
                    requestHeaders.Length));

                await codec.WriteFrameAsync(
                    BuildResponseHeadersFrame(requestHeaders.StreamId, 200),
                    ct);

                var bodyBytes = Encoding.ASCII.GetBytes(responseBodies[i]);
                await codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = requestHeaders.StreamId,
                    Payload = bodyBytes,
                    Length = bodyBytes.Length
                }, ct);
            }
        }

        private static async Task<Http2FrameCodec> CompleteHttp2HandshakeAsync(
            Stream serverStream,
            CancellationToken ct)
        {
            await ReadExactlyAsync(serverStream, new byte[Http2Constants.ConnectionPreface.Length], ct);

            var codec = new Http2FrameCodec(serverStream);
            await codec.ReadFrameAsync(16384, ct);
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.Ack,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);
            await codec.ReadFrameAsync(16384, ct);
            return codec;
        }

        private static async Task<Http2Frame> ReadNextHeadersFrameAsync(
            Http2FrameCodec codec,
            CancellationToken ct)
        {
            while (true)
            {
                var frame = await codec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.Headers)
                    return frame;
            }
        }

        private static Http2Frame BuildResponseHeadersFrame(
            int streamId,
            int statusCode)
        {
            var encoder = new HpackEncoder();
            var headerBlock = encoder.Encode(new List<(string, string)>
            {
                (":status", statusCode.ToString())
            }).ToArray();

            return new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = Http2FrameFlags.EndHeaders,
                StreamId = streamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            };
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, read, buffer.Length - read, ct);
                if (n == 0)
                    throw new IOException("Unexpected end of stream.");

                read += n;
            }
        }

        private static void AssertHeader(
            List<(string Name, string Value)> headers,
            string name,
            string value)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Name, name, StringComparison.Ordinal) &&
                    string.Equals(header.Value, value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            Assert.Fail($"Expected HTTP/2 header {name}: {value}.");
        }

        private static void AssertNoHeader(
            List<(string Name, string Value)> headers,
            string name)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                    Assert.Fail($"Did not expect HTTP/2 header {name}.");
            }
        }

        private static X509Certificate2 TryCreateLocalhostCertificate()
        {
            try
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=localhost",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));
                request.CertificateExtensions.Add(
                    new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(7));
                return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static async Task WriteResponseAsync(Stream stream, string response)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteResponseHeadAsync(Stream stream, string responseHead)
        {
            var bytes = Encoding.ASCII.GetBytes(responseHead);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)> ReadRequestStartAndHeadersAsync(
            Stream stream,
            CancellationToken ct)
        {
            var requestLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    break;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return (requestLine, headers);
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)?> TryReadRequestStartAndHeadersAsync(
            Stream stream,
            TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var request = await ReadRequestStartAndHeadersAsync(stream, cts.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(request.RequestLine))
                    return null;

                return request;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var bytes = new List<byte>(128);
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, 1, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (buffer[0] == (byte)'\n')
                    break;
                if (buffer[0] != (byte)'\r')
                    bytes.Add(buffer[0]);
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
