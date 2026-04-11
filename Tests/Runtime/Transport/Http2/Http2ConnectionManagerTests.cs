using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2ConnectionManagerTests
    {
        private const string OriginHost = "api.example.com";
        private const int OriginPort = 443;
        private const string ProxyAHost = "proxy-a.example.com";
        private const int ProxyAPort = 8080;
        private const string ProxyBHost = "proxy-b.example.com";
        private const int ProxyBPort = 8081;

        [Test]
        public void GetOrCreateAsync_DirectAndTunnelKeys_AreIndependentEntries()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();

                var direct = await CreateDirectConnectionAsync(manager, OriginHost, OriginPort, cts.Token);
                var tunnel = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);

                Assert.AreNotSame(direct, tunnel);
                Assert.AreSame(direct, manager.GetIfExists(OriginHost, OriginPort));
                Assert.AreSame(tunnel, manager.GetIfExists(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
                Assert.AreEqual(OriginHost, tunnel.Host);
                Assert.AreEqual(OriginPort, tunnel.Port);

                var keys = GetConnectionKeys(manager);
                CollectionAssert.Contains(keys, $"{OriginHost}:{OriginPort}");
                CollectionAssert.Contains(keys, $"{OriginHost}:{OriginPort}|via|{ProxyAHost}:{ProxyAPort}");
            });
        }

        [Test]
        public void GetOrCreateAsync_SameOriginDifferentProxies_AreIndependentEntries()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();

                var proxyA = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);
                var proxyB = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyBHost,
                    ProxyBPort,
                    cts.Token);

                Assert.AreNotSame(proxyA, proxyB);
                Assert.AreSame(proxyA, manager.GetIfExists(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
                Assert.AreSame(proxyB, manager.GetIfExists(OriginHost, OriginPort, ProxyBHost, ProxyBPort));
            });
        }

        [Test]
        public void GetIfExists_DirectAndTunnelKeys_DoNotAlias()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);

                using (var directOnlyManager = CreateManager())
                {
                    await CreateDirectConnectionAsync(directOnlyManager, OriginHost, OriginPort, cts.Token);

                    Assert.IsNotNull(directOnlyManager.GetIfExists(OriginHost, OriginPort));
                    Assert.IsNull(directOnlyManager.GetIfExists(
                        OriginHost,
                        OriginPort,
                        ProxyAHost,
                        ProxyAPort));
                }

                using (var tunnelOnlyManager = CreateManager())
                {
                    await CreateTunnelConnectionAsync(
                        tunnelOnlyManager,
                        OriginHost,
                        OriginPort,
                        ProxyAHost,
                        ProxyAPort,
                        cts.Token);

                    Assert.IsNull(tunnelOnlyManager.GetIfExists(OriginHost, OriginPort));
                    Assert.IsNotNull(tunnelOnlyManager.GetIfExists(
                        OriginHost,
                        OriginPort,
                        ProxyAHost,
                        ProxyAPort));
                }
            });
        }

        [Test]
        public void Remove_TunnelKey_DoesNotAffectDirectConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();

                var direct = await CreateDirectConnectionAsync(manager, OriginHost, OriginPort, cts.Token);
                var tunnel = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);

                manager.Remove(OriginHost, OriginPort, ProxyAHost, ProxyAPort);

                Assert.AreSame(direct, manager.GetIfExists(OriginHost, OriginPort));
                Assert.IsNull(manager.GetIfExists(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
                Assert.IsFalse(tunnel.IsAlive);
            });
        }

        [Test]
        public void GetOrCreateAsync_CanceledBeforeSlowPathLock_DisposesUnownedStream()
        {
            AssertAsync.Run(async () =>
            {
                using var manager = CreateManager();
                var duplex = new TestDuplexStream();
                var trackingStream = new DisposeTrackingStream(duplex.ClientStream);
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                try
                {
                    await manager.GetOrCreateAsync(
                        OriginHost,
                        OriginPort,
                        ProxyAHost,
                        ProxyAPort,
                        trackingStream,
                        cts.Token);
                    Assert.Fail("Expected cancellation before HTTP/2 connection creation.");
                }
                catch (OperationCanceledException)
                {
                }

                Assert.IsTrue(trackingStream.DisposeCalled);
                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
            });
        }

        [Test]
        public void Http2Connection_Dispose_DisposesTunnelStreamChainOnce()
        {
            var proxyTcpStream = new DisposeTrackingStream(new MemoryStream());
            var tunnelStream = new DisposeTrackingStream(proxyTcpStream);
            var tlsStream = new DisposeTrackingStream(tunnelStream);
            var connection = new Http2Connection(
                tlsStream,
                OriginHost,
                OriginPort,
                new Http2Options(),
                new StreamingOptions());

            Assert.DoesNotThrow(() => connection.Dispose());
            Assert.DoesNotThrow(() => connection.Dispose());

            Assert.AreEqual(1, tlsStream.DisposeCallCount);
            Assert.AreEqual(1, tunnelStream.DisposeCallCount);
            Assert.AreEqual(1, proxyTcpStream.DisposeCallCount);
        }

        [Test]
        public void GoawayThroughTunnel_FailsPendingStreamAndManagerReturnsNull()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();
                var tunnel = await CreateTunnelConnectionWithServerAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);
                var serverCodec = new Http2FrameCodec(tunnel.ServerStream);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri($"https://{OriginHost}/goaway"));
                var context = new RequestContext(request);
                var responseTask = tunnel.Connection.SendRequestAsync(
                    request,
                    context,
                    cts.Token).AsTask();

                var requestHeaders = await ReadNextHeadersFrameAsync(serverCodec, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, requestHeaders.Type);

                await serverCodec.WriteFrameAsync(
                    BuildGoAwayFrame(lastStreamId: 0, errorCode: 0),
                    cts.Token);

                var error = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await responseTask);
                Assert.AreEqual(UHttpErrorType.NetworkError, error.HttpError.Type);
                StringAssert.Contains("GOAWAY", error.Message);

                Assert.IsFalse(tunnel.Connection.IsAlive);
                Assert.IsNull(manager.GetIfExists(
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort));

                var replacement = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);

                Assert.AreNotSame(tunnel.Connection, replacement);
                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
            });
        }

        [Test]
        public void GetOrCreateAsync_ConcurrentTunnelCreation_RetainsOneConnectionAndDisposesDiscardedStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();
                var firstDuplex = new TestDuplexStream();
                var secondDuplex = new TestDuplexStream();
                var secondStream = new DisposeTrackingStream(secondDuplex.ClientStream);

                Assert.IsNull(manager.GetIfExists(OriginHost, OriginPort, ProxyAHost, ProxyAPort));

                var firstTask = manager.GetOrCreateAsync(
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    firstDuplex.ClientStream,
                    cts.Token).AsTask();
                var secondTask = manager.GetOrCreateAsync(
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    secondStream,
                    cts.Token).AsTask();

                var firstHandshakeTask = CompleteHandshakeAsync(firstDuplex.ServerStream, cts.Token);
                var first = await firstTask;
                await firstHandshakeTask;
                var second = await secondTask;

                Assert.AreSame(first, second);
                Assert.AreSame(first, manager.GetIfExists(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
                Assert.AreEqual(1, secondStream.DisposeCallCount);

                var tunnelKey = $"{OriginHost}:{OriginPort}|via|{ProxyAHost}:{ProxyAPort}";
                Assert.AreEqual(1, GetConnectionKeys(manager).Count(k => k == tunnelKey));
            });
        }

        [Test]
        public void HasConnection_TunnelOverload_TracksLifecycleWithoutAffectingDirectConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = CreateManager();

                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort));
                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));

                var direct = await CreateDirectConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    cts.Token);
                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort));
                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));

                var tunnel = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);
                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort));
                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));

                manager.Remove(OriginHost, OriginPort, ProxyAHost, ProxyAPort);
                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort));
                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
                Assert.IsTrue(direct.IsAlive);
                Assert.IsFalse(tunnel.IsAlive);

                var replacement = await CreateTunnelConnectionAsync(
                    manager,
                    OriginHost,
                    OriginPort,
                    ProxyAHost,
                    ProxyAPort,
                    cts.Token);
                replacement.Dispose();

                Assert.IsTrue(manager.HasConnection(OriginHost, OriginPort));
                Assert.IsFalse(manager.HasConnection(OriginHost, OriginPort, ProxyAHost, ProxyAPort));
            });
        }

        private static Http2ConnectionManager CreateManager()
        {
            return new Http2ConnectionManager(new Http2Options(), new StreamingOptions());
        }

        private static async Task<Http2Connection> CreateDirectConnectionAsync(
            Http2ConnectionManager manager,
            string host,
            int port,
            CancellationToken ct)
        {
            var duplex = new TestDuplexStream();
            var serverTask = CompleteHandshakeAsync(duplex.ServerStream, ct);
            var connection = await manager.GetOrCreateAsync(host, port, duplex.ClientStream, ct);
            await serverTask;
            return connection;
        }

        private static async Task<Http2Connection> CreateTunnelConnectionAsync(
            Http2ConnectionManager manager,
            string originHost,
            int originPort,
            string proxyHost,
            int proxyPort,
            CancellationToken ct)
        {
            var tunnel = await CreateTunnelConnectionWithServerAsync(
                manager,
                originHost,
                originPort,
                proxyHost,
                proxyPort,
                ct);
            return tunnel.Connection;
        }

        private static async Task<(Http2Connection Connection, Stream ServerStream, TestDuplexStream Duplex)>
            CreateTunnelConnectionWithServerAsync(
                Http2ConnectionManager manager,
                string originHost,
                int originPort,
                string proxyHost,
                int proxyPort,
                CancellationToken ct)
        {
            var duplex = new TestDuplexStream();
            var serverTask = CompleteHandshakeAsync(duplex.ServerStream, ct);
            var connection = await manager.GetOrCreateAsync(
                originHost,
                originPort,
                proxyHost,
                proxyPort,
                duplex.ClientStream,
                ct);
            await serverTask;
            return (connection, duplex.ServerStream, duplex);
        }

        private static async Task CompleteHandshakeAsync(Stream serverStream, CancellationToken ct)
        {
            await ReadExactlyAsync(serverStream, new byte[Http2Constants.ConnectionPreface.Length], ct);

            var serverCodec = new Http2FrameCodec(serverStream);
            await serverCodec.ReadFrameAsync(16384, ct);
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.Ack,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);
            await serverCodec.ReadFrameAsync(16384, ct);
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

        private static string[] GetConnectionKeys(Http2ConnectionManager manager)
        {
            var field = typeof(Http2ConnectionManager).GetField(
                "_connections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field);

            var connections = (ConcurrentDictionary<string, Http2Connection>)field.GetValue(manager);
            return connections.Keys.ToArray();
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

        private static Http2Frame BuildGoAwayFrame(int lastStreamId, uint errorCode)
        {
            var payload = new byte[8];
            payload[0] = (byte)((lastStreamId >> 24) & 0x7F);
            payload[1] = (byte)((lastStreamId >> 16) & 0xFF);
            payload[2] = (byte)((lastStreamId >> 8) & 0xFF);
            payload[3] = (byte)(lastStreamId & 0xFF);
            payload[4] = (byte)((errorCode >> 24) & 0xFF);
            payload[5] = (byte)((errorCode >> 16) & 0xFF);
            payload[6] = (byte)((errorCode >> 8) & 0xFF);
            payload[7] = (byte)(errorCode & 0xFF);

            return new Http2Frame
            {
                Type = Http2FrameType.GoAway,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = payload,
                Length = payload.Length
            };
        }

        private sealed class DisposeTrackingStream : Stream
        {
            private readonly Stream _inner;

            public bool DisposeCalled { get; private set; }
            public int DisposeCallCount { get; private set; }

            public DisposeTrackingStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                _inner.SetLength(value);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DisposeCalled = true;
                    DisposeCallCount++;
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
