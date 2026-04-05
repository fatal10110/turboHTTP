using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
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
