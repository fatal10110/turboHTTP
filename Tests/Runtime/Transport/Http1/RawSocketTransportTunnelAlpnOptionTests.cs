using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #103: HTTP/2 inside CONNECT tunnels is opt-in (<see cref="Http2Options.EnableHttp2ThroughConnectTunnels"/>),
    /// plus the phase 22c.4 tunnel checks that need a full transport round trip: an explicitly
    /// selected BouncyCastle provider (mock) yielding h2 through a tunnel, a new tunnel after GOAWAY,
    /// concurrent tunnel establishment, and a platform TLS failure without a provider switch.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportTunnelAlpnOptionTests
    {
        private const string OriginHost = "origin.example";
        private const int OriginPort = 443;

        private ITlsProvider _previousBouncyCastle;

        [SetUp]
        public void SetUp()
        {
            _previousBouncyCastle = TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(null);
        }

        [TearDown]
        public void TearDown()
        {
            TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(_previousBouncyCastle);
        }

        [Test]
        public void Http2Options_TunnelOption_DefaultsOff_ClonesAndLeavesDefault()
        {
            var options = new Http2Options();
            Assert.IsFalse(options.EnableHttp2ThroughConnectTunnels);
            Assert.IsTrue(options.IsDefault());

            options.EnableHttp2ThroughConnectTunnels = true;
            Assert.IsFalse(options.IsDefault(), "A non-default value must route the client to the configured transport factory (#275).");

            var clone = options.Clone();
            Assert.IsTrue(clone.EnableHttp2ThroughConnectTunnels);
            Assert.IsFalse(clone.IsDefault());

            var clientOptions = new UHttpClientOptions();
            clientOptions.Http2.EnableHttp2ThroughConnectTunnels = true;
            Assert.IsTrue(clientOptions.Clone().Http2.EnableHttp2ThroughConnectTunnels);
        }

        [Test]
        public void ConnectTunnelAlpn_OffersSharedListsPerOption()
        {
            using var defaultTransport = new RawSocketTransport();
            using var enabledTransport = new RawSocketTransport(
                null,
                TlsBackend.Auto,
                new Http2Options { EnableHttp2ThroughConnectTunnels = true });

            Assert.AreSame(AlpnProtocols.Http11Only, defaultTransport.ConnectTunnelAlpnProtocols);
            Assert.AreSame(AlpnProtocols.H2AndHttp11, enabledTransport.ConnectTunnelAlpnProtocols);
            CollectionAssert.AreEqual(new[] { "http/1.1" }, AlpnProtocols.Http11Only);
            CollectionAssert.AreEqual(new[] { "h2", "http/1.1" }, AlpnProtocols.H2AndHttp11);
        }

        [Test]
        public void HttpsViaProxy_Default_OffersOnlyHttp11AndUsesHttp11InTunnel()
        {
            AssertAsync.Run(async () =>
            {
                var provider = InstallMockBouncyCastle(MockAlpnMode.HonorOffer);
                using var proxy = new TunnelOriginProxy();
                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.BouncyCastle);
                using var client = CreateClient(transport, proxy.Port);

                using (var response = await client.Get($"https://{OriginHost}/default").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("http/1.1:/default", response.GetBodyAsString());
                }

                Assert.AreEqual(1, provider.Offers.Count);
                Assert.AreSame(AlpnProtocols.Http11Only, provider.Offers.Single(),
                    "By default the tunnel must offer only http/1.1.");
                CollectionAssert.AreEqual(new[] { "http/1.1" }, proxy.Protocols.ToArray());
                Assert.IsFalse(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));
            });
        }

        [Test]
        public void HttpsViaProxy_Default_ProviderReportingUnofferedH2_StaysOnHttp11()
        {
            AssertAsync.Run(async () =>
            {
                InstallMockBouncyCastle(MockAlpnMode.AlwaysH2);
                using var proxy = new TunnelOriginProxy();
                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.BouncyCastle);
                using var client = CreateClient(transport, proxy.Port);

                using (var response = await client.Get($"https://{OriginHost}/forced").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("http/1.1:/forced", response.GetBodyAsString());
                }

                CollectionAssert.AreEqual(new[] { "http/1.1" }, proxy.Protocols.ToArray());
                Assert.IsFalse(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));
            });
        }

        [Test]
        public void HttpsViaProxy_Enabled_ExplicitBouncyCastleMockNegotiatesH2_AndReusesTunnelConnection()
        {
            AssertAsync.Run(async () =>
            {
                var provider = InstallMockBouncyCastle(MockAlpnMode.HonorOffer);
                using var proxy = new TunnelOriginProxy();
                using var transport = CreateEnabledTransport();
                using var client = CreateClient(transport, proxy.Port);

                using (var first = await client
                           .Get($"https://{OriginHost}/first")
                           .WithHeader("Proxy-Authorization", "Basic should-not-leak")
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                    Assert.AreEqual("h2:/first", first.GetBodyAsString());
                }

                Assert.IsTrue(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));
                Assert.IsFalse(transport.HasHttp2Connection(OriginHost, OriginPort));

                using (var second = await client.Get($"https://{OriginHost}/second").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                    Assert.AreEqual("h2:/second", second.GetBodyAsString());
                }

                Assert.AreEqual(1, provider.Offers.Count);
                Assert.AreSame(AlpnProtocols.H2AndHttp11, provider.Offers.Single());
                Assert.AreEqual(1, proxy.ConnectRequests.Count, "The second request must reuse the tunneled HTTP/2 connection.");
                StringAssert.StartsWith($"CONNECT {OriginHost}:{OriginPort} HTTP/1.1", proxy.ConnectRequests.Single());
                CollectionAssert.AreEqual(new[] { "h2" }, proxy.Protocols.ToArray());

                var requests = proxy.Http2Requests.ToArray();
                Assert.AreEqual(2, requests.Length);
                Assert.IsFalse(requests.Any(r => r.Any(h => h.Name == "proxy-authorization")),
                    "Proxy-Authorization must not reach the origin over the tunneled HTTP/2 connection.");
            });
        }

        [Test]
        public void HttpsViaProxy_Enabled_GoawayThroughTunnel_NextRequestOpensNewTunnel()
        {
            AssertAsync.Run(async () =>
            {
                InstallMockBouncyCastle(MockAlpnMode.HonorOffer);
                using var proxy = new TunnelOriginProxy { GoAwayOnFirstTunnel = true };
                using var transport = CreateEnabledTransport();
                using var client = CreateClient(transport, proxy.Port);

                // The server sends GOAWAY (last stream 1) before stream 1's response, so the client
                // has processed it by the time the response completes (one read loop, in order).
                using (var first = await client.Get($"https://{OriginHost}/before-goaway").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                    Assert.AreEqual("h2:/before-goaway", first.GetBodyAsString());
                }

                Assert.IsFalse(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port),
                    "A GOAWAY'd tunnel connection must not take new requests.");

                using (var second = await client.Get($"https://{OriginHost}/after-goaway").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                    Assert.AreEqual("h2:/after-goaway", second.GetBodyAsString());
                }

                Assert.AreEqual(2, proxy.ConnectRequests.Count, "The request after GOAWAY must establish a new tunnel.");
                CollectionAssert.AreEqual(new[] { "h2", "h2" }, proxy.Protocols.ToArray());
                Assert.IsTrue(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));
            });
        }

        [Test]
        public void HttpsViaProxy_Enabled_ConcurrentTunnelEstablishment_RetainsOneConnectionAndDisposesDiscardedTunnel()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                // Both handshakes wait until both requests reached the TLS step: each passed the
                // empty HTTP/2 fast path and opened its own CONNECT tunnel.
                var provider = InstallMockBouncyCastle(MockAlpnMode.HonorOffer, gateCount: 2, gateToken: cts.Token);
                using var proxy = new TunnelOriginProxy();
                using var pool = new TcpConnectionPool(maxConnectionsPerHost: 4, tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(
                    pool,
                    TlsBackend.BouncyCastle,
                    new Http2Options { EnableHttp2ThroughConnectTunnels = true });
                using var client = CreateClient(transport, proxy.Port);

                var firstTask = client.Get($"https://{OriginHost}/a").SendBufferedAsync(cts.Token).AsTask();
                var secondTask = client.Get($"https://{OriginHost}/b").SendBufferedAsync(cts.Token).AsTask();

                using var first = await firstTask;
                using var second = await secondTask;

                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual("h2:/a", first.GetBodyAsString());
                Assert.AreEqual("h2:/b", second.GetBodyAsString());

                Assert.AreEqual(2, proxy.ConnectRequests.Count);
                Assert.AreEqual(2, provider.Streams.Count);
                CollectionAssert.AreEqual(new[] { "h2" }, proxy.Protocols.ToArray(),
                    "Only one tunnel may carry the HTTP/2 connection.");
                Assert.AreEqual(2, proxy.Http2Requests.Count);
                Assert.IsTrue(transport.HasHttp2Connection(OriginHost, OriginPort, "127.0.0.1", proxy.Port));

                // The discarded tunnel's stream was disposed before its request was dispatched on
                // the retained connection; the retained one stays open.
                var disposeCounts = provider.Streams.Select(s => s.DisposeCount).OrderBy(c => c).ToArray();
                CollectionAssert.AreEqual(new[] { 0, 1 }, disposeCounts);
            });
        }

        [Test]
        public void HttpsViaProxy_TunnelTlsFailure_PropagatesWithoutNewTunnelOrProviderSwitch()
        {
            AssertAsync.Run(async () =>
            {
                var bouncyCastle = InstallMockBouncyCastle(MockAlpnMode.HonorOffer);
                using var proxy = new TunnelOriginProxy { CloseAfterConnect = true };
                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);
                using var client = CreateClient(transport, proxy.Port);

                Exception failure = null;
                try
                {
                    using var response = await client.Get($"https://{OriginHost}/tls-fails").SendBufferedAsync();
                    Assert.Fail("The TLS handshake inside the tunnel must fail.");
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure);
                Assert.AreEqual(1, proxy.ConnectRequests.Count, "A TLS failure must not open a fresh tunnel.");
                Assert.AreEqual(0, bouncyCastle.Offers.Count, "A TLS failure must not switch to another provider.");
            });
        }

        [Test]
        public void EstablishConnectTunnelAsync_PlatformTlsException_PropagatesWithoutProviderSwitch()
        {
            AssertAsync.Run(async () =>
            {
                if (!TlsProviderSelector.GetProvider(TlsBackend.SslStream).IsAlpnSupported())
                    TestRequirements.IgnoreEnvironment("SslStream ALPN is unavailable in this runtime.");

                var bouncyCastle = InstallMockBouncyCastle(MockAlpnMode.HonorOffer);
                using var transport = new RawSocketTransport(tlsBackend: TlsBackend.SslStream);
                var stream = new ConnectThenPlatformFailureStream();

                Exception failure = null;
                try
                {
                    var method = typeof(RawSocketTransport).GetMethod(
                        "EstablishConnectTunnelAsync",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.IsNotNull(method);
                    await (Task<TlsResult>)method.Invoke(
                        transport,
                        new object[] { stream, OriginHost, OriginPort, null, false, CancellationToken.None });
                    Assert.Fail("Expected the platform TLS failure.");
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure);
                Assert.IsFalse(failure is UHttpException);
                StringAssert.Contains(ConnectThenPlatformFailureStream.Message, failure.ToString());
                Assert.AreEqual(1, stream.ConnectRequestCount, "Exactly one CONNECT; no retry on the stream.");
                Assert.AreEqual(0, bouncyCastle.Offers.Count, "A platform TLS failure must not switch to BouncyCastle.");
            });
        }

        private static RawSocketTransport CreateEnabledTransport()
        {
            return new RawSocketTransport(
                null,
                TlsBackend.BouncyCastle,
                new Http2Options { EnableHttp2ThroughConnectTunnels = true });
        }

        private static UHttpClient CreateClient(RawSocketTransport transport, int proxyPort)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = transport,
                DisposeTransport = false,
                Proxy = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyPort}"),
                    UseEnvironmentVariables = false
                }
            });
        }

        private static MockBouncyCastleProvider InstallMockBouncyCastle(
            MockAlpnMode mode,
            int gateCount = 0,
            CancellationToken gateToken = default)
        {
            var provider = new MockBouncyCastleProvider(mode, gateCount, gateToken);
            TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(provider);
            return provider;
        }

        private enum MockAlpnMode
        {
            // Selects h2 only when offered, like an h2-capable origin (RFC 7301 section 3.2).
            HonorOffer,
            // Reports h2 whatever was offered (a non-conforming provider or peer).
            AlwaysH2
        }

        /// <summary>
        /// BouncyCastle stand-in: no encryption, the tunnel bytes pass through; records the ALPN offer
        /// (the shared array instance) and the streams it returned.
        /// </summary>
        private sealed class MockBouncyCastleProvider : ITlsProvider
        {
            private readonly MockAlpnMode _mode;
            private readonly int _gateCount;
            private readonly CancellationToken _gateToken;
            private readonly TaskCompletionSource<bool> _gate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _entered;

            public readonly ConcurrentQueue<string[]> Offers = new ConcurrentQueue<string[]>();
            public readonly ConcurrentQueue<DisposeCountingStream> Streams = new ConcurrentQueue<DisposeCountingStream>();

            public MockBouncyCastleProvider(MockAlpnMode mode, int gateCount, CancellationToken gateToken)
            {
                _mode = mode;
                _gateCount = gateCount;
                _gateToken = gateToken;
            }

            public string ProviderName => "MockBouncyCastle";

            public bool IsAlpnSupported() => true;

            public async Task<TlsResult> WrapAsync(
                Stream innerStream,
                string host,
                string[] alpnProtocols,
                CancellationToken ct)
            {
                Offers.Enqueue(alpnProtocols);
                if (_gateCount > 0)
                {
                    if (Interlocked.Increment(ref _entered) >= _gateCount)
                        _gate.TrySetResult(true);

                    using (_gateToken.Register(() => _gate.TrySetCanceled()))
                        await _gate.Task.ConfigureAwait(false);
                }

                var negotiated = _mode == MockAlpnMode.AlwaysH2 || Array.IndexOf(alpnProtocols, "h2") >= 0
                    ? "h2"
                    : "http/1.1";
                var stream = new DisposeCountingStream(innerStream);
                Streams.Enqueue(stream);
                return new TlsResult(stream, negotiated, "1.3", providerName: ProviderName);
            }
        }

        private sealed class DisposeCountingStream : Stream
        {
            private readonly Stream _inner;
            private int _disposeCount;

            public DisposeCountingStream(Stream inner)
            {
                _inner = inner;
            }

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.WriteAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.WriteAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Increment(ref _disposeCount) == 1)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Answers the CONNECT with 200, then fails every read and write (the TLS handshake) with
        /// <see cref="PlatformNotSupportedException"/>, as a runtime without working SslStream does.
        /// </summary>
        private sealed class ConnectThenPlatformFailureStream : Stream
        {
            public const string Message = "Injected platform TLS failure.";

            private readonly byte[] _connectResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 Connection Established\r\n\r\n");
            private int _responseOffset;
            private bool _tunnelOpen;
            private int _connectRequestCount;

            public int ConnectRequestCount => _connectRequestCount;

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

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_tunnelOpen)
                    throw new PlatformNotSupportedException(Message);

                var toCopy = Math.Min(_connectResponse.Length - _responseOffset, count);
                Buffer.BlockCopy(_connectResponse, _responseOffset, buffer, offset, toCopy);
                _responseOffset += toCopy;
                if (_responseOffset >= _connectResponse.Length)
                    _tunnelOpen = true;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer, offset, count));

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_tunnelOpen)
                    throw new PlatformNotSupportedException(Message);

                if (Encoding.ASCII.GetString(buffer, offset, count).StartsWith("CONNECT ", StringComparison.Ordinal))
                    _connectRequestCount++;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// A loopback CONNECT proxy that is also the origin: it answers CONNECT with 200 and then
        /// serves the tunnel itself in plaintext (the mock TLS provider passes bytes through),
        /// HTTP/2 when the client sends the connection preface, HTTP/1.1 otherwise. Each response
        /// body is "&lt;protocol&gt;:&lt;path&gt;".
        /// </summary>
        private sealed class TunnelOriginProxy : IDisposable
        {
            private const int MaxLineBytes = 8 * 1024;
            private const int MaxHeadLines = 100;

            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _gate = new object();
            private int _acceptCount;

            public readonly ConcurrentQueue<string> ConnectRequests = new ConcurrentQueue<string>();
            public readonly ConcurrentQueue<string> Protocols = new ConcurrentQueue<string>();
            public readonly ConcurrentQueue<List<(string Name, string Value)>> Http2Requests =
                new ConcurrentQueue<List<(string Name, string Value)>>();

            public bool GoAwayOnFirstTunnel { get; set; }
            public bool CloseAfterConnect { get; set; }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public TunnelOriginProxy()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
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
                var ct = _cts.Token;
                try
                {
                    var stream = client.GetStream();
                    var connectHead = await ReadHeadAsync(stream, ct);
                    ConnectRequests.Enqueue(connectHead);
                    await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
                    if (CloseAfterConnect)
                        return;

                    var firstLine = await ReadLineAsync(stream, ct);
                    if (firstLine == "PRI * HTTP/2.0")
                    {
                        // The rest of the preface: "\r\nSM\r\n\r\n".
                        await ReadExactlyAsync(stream, new byte[8], ct);
                        Protocols.Enqueue("h2");
                        await ServeHttp2Async(stream, GoAwayOnFirstTunnel && index == 1, ct);
                    }
                    else
                    {
                        await ReadHeadAsync(stream, ct);
                        Protocols.Enqueue("http/1.1");
                        var path = firstLine.Split(' ')[1];
                        var body = "http/1.1:" + path;
                        await WriteAsciiAsync(
                            stream,
                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}",
                            ct);
                    }
                }
                catch
                {
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }

            private async Task ServeHttp2Async(Stream stream, bool goAwayFirst, CancellationToken ct)
            {
                var codec = new Http2FrameCodec(stream);
                var decoder = new HpackDecoder();
                var encoder = new HpackEncoder();
                await codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);

                while (true)
                {
                    var frame = await codec.ReadFrameAsync(16384, ct);
                    if (frame.Type == Http2FrameType.Settings && !frame.HasFlag(Http2FrameFlags.Ack))
                    {
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Settings,
                            Flags = Http2FrameFlags.Ack,
                            StreamId = 0,
                            Payload = Array.Empty<byte>(),
                            Length = 0
                        }, ct);
                        continue;
                    }

                    if (frame.Type != Http2FrameType.Headers)
                        continue;

                    var headers = decoder.Decode(frame.Payload, 0, frame.Length);
                    Http2Requests.Enqueue(headers);
                    var path = headers.First(h => h.Name == ":path").Value;

                    if (goAwayFirst)
                    {
                        goAwayFirst = false;
                        // RFC 9113 section 6.8: last stream id = this stream, NO_ERROR.
                        var payload = new byte[8];
                        payload[0] = (byte)((frame.StreamId >> 24) & 0x7F);
                        payload[1] = (byte)(frame.StreamId >> 16);
                        payload[2] = (byte)(frame.StreamId >> 8);
                        payload[3] = (byte)frame.StreamId;
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.GoAway,
                            Flags = Http2FrameFlags.None,
                            StreamId = 0,
                            Payload = payload,
                            Length = payload.Length
                        }, ct);
                    }

                    var headerBlock = encoder.Encode(new List<(string, string)> { (":status", "200") }).ToArray();
                    await codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Headers,
                        Flags = Http2FrameFlags.EndHeaders,
                        StreamId = frame.StreamId,
                        Payload = headerBlock,
                        Length = headerBlock.Length
                    }, ct);

                    var body = Encoding.ASCII.GetBytes("h2:" + path);
                    await codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Data,
                        Flags = Http2FrameFlags.EndStream,
                        StreamId = frame.StreamId,
                        Payload = body,
                        Length = body.Length
                    }, ct);
                }
            }

            private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
            {
                var head = new StringBuilder();
                for (int i = 0; i < MaxHeadLines; i++)
                {
                    var line = await ReadLineAsync(stream, ct);
                    if (line.Length == 0)
                        return head.ToString();
                    head.Append(line).Append("\r\n");
                }

                throw new IOException("Request head too long.");
            }

            private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
            {
                var bytes = new List<byte>();
                var one = new byte[1];
                while (bytes.Count < MaxLineBytes)
                {
                    if (await stream.ReadAsync(one, 0, 1, ct) == 0)
                        throw new IOException("Unexpected end of stream.");
                    if (one[0] == (byte)'\n' && bytes.Count > 0 && bytes[bytes.Count - 1] == (byte)'\r')
                        return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 1);
                    bytes.Add(one[0]);
                }

                throw new IOException("Line too long.");
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

            private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                await stream.FlushAsync(ct);
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
    }
}
