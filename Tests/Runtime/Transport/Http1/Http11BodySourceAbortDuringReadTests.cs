using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// HTTP/1.1 body-source ownership: Abort during a pending read without a cancellable token (#656),
    /// DisposeAsync during a pending read (#639), and bytes left in the reader after a complete
    /// response (#643). <see cref="AbandonedReadStream"/> ignores both the token and Dispose, as Mono
    /// and IL2CPP socket reads sometimes do.
    /// </summary>
    [TestFixture]
    public class Http11BodySourceAbortDuringReadTests
    {
        // Guard only: the read must end long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        // Guard only, below Guard so the settings are restored even when the wait fails.
        private static readonly TimeSpan SettleGuard = TimeSpan.FromSeconds(5);

        [TestCase(false)]
        [TestCase(true)]
        public void Abort_PendingReadWithoutToken_FailsTheReadAndKeepsItsBufferOutOfThePool(bool chunked)
        {
            var kind = chunked ? Http11ResponseBodyKind.Chunked : Http11ResponseBodyKind.ContentLength;
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                var source = NewSource(stream, socket, kind, kind == Http11ResponseBodyKind.ContentLength ? 100 : (long?)null);

                // Below the reader's buffer size: the read (or the chunk-size line read) targets the
                // reader's pooled buffer.
                var read = source.ReadAsync(new byte[100], CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);

                source.Abort();
                // Same thread, no await since Abort: a buffer Abort returned is the next one rented.
                AssertNotPooled(stream.ReadBuffer);

                Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(read).ConfigureAwait(false));
                Assert.AreEqual(1, stream.ReadCount, "Abort must not start another read");

                // The platform ends the abandoned read late; its result is observed and dropped.
                stream.EndRead(0);
                await source.DisposeAsync().ConfigureAwait(false);
            }, Guard);
        }

        [Test]
        public void Abort_PendingLargeReadWithoutToken_NeverTargetsCallerMemory()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100_000);
                var callerBuffer = new byte[16 * 1024];

                var read = source.ReadAsync(callerBuffer, CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                Assert.AreNotSame(callerBuffer, stream.ReadBuffer,
                    "an abortable read must target reader-owned memory, never the caller's");

                source.Abort();
                AssertNotPooled(stream.ReadBuffer);
                Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(read).ConfigureAwait(false));
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void Abort_PendingReadWithoutToken_CompletesWhenTheReadLaterEnds()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100);

                var read = source.ReadAsync(new byte[100], CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                source.Abort();
                Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(read).ConfigureAwait(false));

                // A late fault of the abandoned read is observed, never rethrown into a later read.
                stream.FailRead(new IOException("late"));
                Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(
                    source.ReadAsync(new byte[10], CancellationToken.None).AsTask()).ConfigureAwait(false));
            }, Guard);
        }

        [Test]
        public void DisposeAsync_PendingChunkedKeepAliveRead_AbortsWithoutStartingASecondRead()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                // Chunked keep-alive always attempts the dispose drain, which would re-enter the reader.
                var source = NewSource(stream, socket, Http11ResponseBodyKind.Chunked, null, keepAlive: true);

                var read = source.ReadAsync(new byte[100], CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);

                await source.DisposeAsync().ConfigureAwait(false);
                Assert.AreEqual(1, stream.ReadCount, "DisposeAsync started a second read while one was pending");

                Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(read).ConfigureAwait(false));
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ReadAsync_WhileAnotherReadIsPending_IsRefusedWithoutTouchingTheStream()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100);

                var read = source.ReadAsync(new byte[100], CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);

                Assert.IsInstanceOf<InvalidOperationException>(await CaptureAsync(
                    source.ReadAsync(new byte[100], CancellationToken.None).AsTask()).ConfigureAwait(false));
                Assert.AreEqual(1, stream.ReadCount);

                // The first read still owns the reader and completes normally.
                stream.EndRead("abc");
                Assert.AreEqual(3, await read.ConfigureAwait(false));
                source.Abort();
            }, Guard);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReadAsync_StreamThrowsSynchronously_EndsTheReadAndLeavesNothingAbandoned(bool abortDuringRead)
        {
            AssertAsync.Run(async () =>
            {
                var savedSettle = AbandonedSocketIo.SettleTimeout;
                // Far beyond the wait below: an entry the read never ends cannot expire in time. Finite,
                // so a failing run does not hold back the sockets of later tests for good.
                AbandonedSocketIo.SettleTimeout = TimeSpan.FromMinutes(1);
                try
                {
                    var stream = new AbandonedReadStream();
                    using var socket = NewSocket();
                    var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100);
                    stream.OnRead = () =>
                    {
                        // Abort racing the read start: Dispose abandons the read before the stream
                        // throws on the socket Abort closed.
                        if (abortDuringRead)
                            source.Abort();
                        throw new IOException("socket closed");
                    };

                    var error = await CaptureAsync(source.ReadAsync(new byte[100], CancellationToken.None).AsTask())
                        .ConfigureAwait(false);
                    Assert.IsNotNull(error, "the read must fail");
                    Assert.AreEqual(1, stream.ReadCount);

                    source.Abort();
                    stream.OnRead = null;

                    // Neither the abandon Dispose began nor a later Dispose may leave an entry
                    // that holds back new sockets.
                    var settled = AbandonedSocketIo.WaitForSettledAsync(CancellationToken.None);
                    var winner = await Task.WhenAny(settled, Task.Delay(SettleGuard)).ConfigureAwait(false);
                    Assert.AreSame(settled, winner, "an abandoned body-read entry was never ended");
                }
                finally
                {
                    AbandonedSocketIo.SettleTimeout = savedSettle;
                }
            }, Guard);
        }

        [Test]
        public void Abort_PendingReadWithCallerToken_DoesNotLeaveTheReaderOnTheCallersToken()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();

                var reader = await AbortThenEndLateAsync(stream, socket, cts.Token).ConfigureAwait(false);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.IsFalse(reader.IsAlive,
                    "the abandoned read's registration on the caller's token still roots the reader");
                GC.KeepAlive(cts);
            }, Guard);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> AbortThenEndLateAsync(
            AbandonedReadStream stream,
            Socket socket,
            CancellationToken callerToken)
        {
            var source = NewTrackedSource(stream, socket, out var weak);
            var read = source.ReadAsync(new byte[100], callerToken).AsTask();
            await stream.ReadPending.ConfigureAwait(false);
            source.Abort();
            Assert.IsInstanceOf<ObjectDisposedException>(await CaptureAsync(read).ConfigureAwait(false));

            // The platform ends the abandoned read late; its continuation runs inline here.
            stream.EndRead(0);
            return weak;
        }

        // Not inlined, so no frame of the test holds the reader itself.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Http11ResponseBodySource NewTrackedSource(
            AbandonedReadStream stream,
            Socket socket,
            out WeakReference reader)
        {
            var bufferedReader = new Http11ResponseParser.BufferedStreamReader(stream);
            reader = new WeakReference(bufferedReader);
            return NewSource(bufferedReader, stream, socket, Http11ResponseBodyKind.ContentLength, 100, keepAlive: false);
        }

        // #643: RFC 9112 Section 9.3. A second response in the same write as the first leaves bytes
        // in the reader once the first is complete; that connection must not be reused.
        [TestCase("HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\na", 1)]
        [TestCase("HTTP/1.1 204 No Content\r\n\r\n", 0)]
        [TestCase("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", 0)]
        public void CompleteBody_BytesAfterTheResponse_ConnectionIsNotPooled(string first, int bodyBytes)
        {
            AssertAsync.Run(async () =>
            {
                const string Second = "HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nb";
                Assert.IsFalse(await CompleteOnLoopbackAsync(first + Second, bodyBytes).ConfigureAwait(false),
                    "a connection with unread bytes after the response went back to the pool");
            }, Guard);
        }

        [TestCase("HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\na", 1)]
        [TestCase("HTTP/1.1 204 No Content\r\n\r\n", 0)]
        public void CompleteBody_NothingAfterTheResponse_ConnectionIsPooled(string response, int bodyBytes)
        {
            AssertAsync.Run(async () =>
            {
                Assert.IsTrue(await CompleteOnLoopbackAsync(response, bodyBytes).ConfigureAwait(false),
                    "a cleanly framed keep-alive response must keep its connection");
            }, Guard);
        }

        [TestCase("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 1\r\n\r\na", true)]
        [TestCase("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n", true)]
        [TestCase("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 1\r\n\r\na", false)]
        public void ProxyChallengeDrain_BytesAfterTheBody_ConnectionIsUnusable(string challenge, bool trailing)
        {
            AssertAsync.Run(async () =>
            {
                var wire = trailing ? challenge + "HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nb" : challenge;
                using var head = await Http11ResponseParser.ParseHeadAsync(
                        new MemoryStream(Encoding.ASCII.GetBytes(wire)), HttpMethod.GET, CancellationToken.None)
                    .ConfigureAwait(false);

                var drained = await RawSocketTransport.TryDrainProxyChallengeBodyAsync(head, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.AreEqual(!trailing, drained);
            }, Guard);
        }

        // Parses `wire` as the response read from a live loopback connection, consumes the body and
        // reports whether the connection went back to the pool.
        private static async Task<bool> CompleteOnLoopbackAsync(string wire, int bodyBytes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = NewSocket();
                client.Connect(new IPEndPoint(IPAddress.Loopback, port));
                using var server = listener.AcceptSocket();
                using var pool = new TcpConnectionPool();

                // The whole wire was already read off the connection, as one write would be.
                var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));
                var head = await Http11ResponseParser.ParseHeadAsync(stream, HttpMethod.GET, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsTrue(head.KeepAlive);

                var lease = new ConnectionLease(
                    pool,
                    new SemaphoreSlim(0, 1),
                    new PooledConnection(client, stream, "127.0.0.1", port, false));
                var source = new Http11ResponseBodySource(head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));

                var buffer = new byte[16];
                int total = 0;
                while (total < bodyBytes)
                {
                    int read = await source.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    Assert.Greater(read, 0);
                    total += read;
                }

                Assert.AreEqual(0, await source.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false));
                await source.DisposeAsync().ConfigureAwait(false);
                return pool.TryGetTlsProviderName("127.0.0.1", out _);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static Socket NewSocket() =>
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static Http11ResponseBodySource NewSource(
            AbandonedReadStream stream,
            Socket socket,
            Http11ResponseBodyKind kind,
            long? contentLength,
            bool keepAlive = false) =>
            NewSource(new Http11ResponseParser.BufferedStreamReader(stream), stream, socket, kind, contentLength, keepAlive);

        private static Http11ResponseBodySource NewSource(
            Http11ResponseParser.BufferedStreamReader reader,
            AbandonedReadStream stream,
            Socket socket,
            Http11ResponseBodyKind kind,
            long? contentLength,
            bool keepAlive)
        {
            var head = new ParsedResponseHead(reader)
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = keepAlive,
                BodyKind = kind,
                ContentLength = contentLength
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "example.test", 80, false));
            return new Http11ResponseBodySource(head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));
        }

        // A buffer a pending read may still write into must not be handed to the next renter.
        private static void AssertNotPooled(byte[] readBuffer)
        {
            Assert.IsNotNull(readBuffer, "no read was issued");
            var rented = ArrayPool<byte>.Shared.Rent(readBuffer.Length);
            try
            {
                Assert.AreNotSame(readBuffer, rented, "the pending read's buffer went back to the pool");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard)).ConfigureAwait(false);
            Assert.AreSame(task, winner, "the read outlived Abort/Dispose");
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
