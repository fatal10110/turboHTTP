using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// Request-side connection reuse: a request that sent "Connection: close" never leaves its
    /// connection in the pool (RFC 9112 Section 9.6, #672), and a failed request write still
    /// reads the server's early final response (RFC 9112 Section 9.5, #837).
    /// </summary>
    public partial class RawSocketTransportConnectionReuseTests
    {
        // --- #672: Connection: close on the request ---

        [TestCase("close")]
        [TestCase("keep-alive, Close")]
        public void RequestConnectionClose_ResponseWithoutConnectionField_ConnectionNotPooled(string connectionValue)
        {
            AssertAsync.Run(async () =>
            {
                using var server = CreateAlwaysKeepAliveServer();
                using var client = CreateClient(new RawSocketTransport());

                using (var first = await client.Get(server.Url("/first"))
                           .WithHeader("Connection", connectionValue)
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                }

                using var second = await client.Get(server.Url("/second")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual(2, server.AcceptCount,
                    "A connection whose request sent Connection: close must not carry another request.");
            });
        }

        [Test]
        public void RequestWithoutConnectionClose_ResponseWithoutConnectionField_ConnectionReused()
        {
            AssertAsync.Run(async () =>
            {
                using var server = CreateAlwaysKeepAliveServer();
                using var client = CreateClient(new RawSocketTransport());

                using (var first = await client.Get(server.Url("/first")).SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                }

                using var second = await client.Get(server.Url("/second")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        // --- #837: early final response after a failed request write (one-shot path) ---

        [Test]
        public void BodyWriteFailure_EarlyFinalResponse_ReturnedAndConnectionNotReused()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    if (index == 1)
                    {
                        await ReadHeadAsync(stream);
                        await WriteAsync(stream,
                            "HTTP/1.1 413 Payload Too Large\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                        if (await TryReadHeadAsync(stream) != null)
                        {
                            await WriteAsync(stream,
                                "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }

                        return;
                    }

                    await ReadHeadAsync(stream);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                var connection = await InjectIdleConnectionAsync(pool, server.Port, tcp => new FailBodyWriteStream(tcp));

                using (var rejected = await client.Post(server.Url("/upload"))
                           .WithBody(new byte[64 * 1024])
                           .SendBufferedAsync())
                {
                    Assert.AreEqual((HttpStatusCode)413, rejected.StatusCode);
                }

                Assert.IsTrue(connection.BodyWriteFailed, "The request body write must have failed.");

                using var next = await client.Get(server.Url("/next")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void BodyWriteFailure_SuccessHead_SendFailureSurfaces()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                var connection = await InjectIdleConnectionAsync(pool, server.Port, tcp => new FailBodyWriteStream(tcp));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await client.Post(server.Url("/upload"))
                        .WithBody(new byte[64 * 1024])
                        .SendBufferedAsync();
                });
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);

                Assert.IsTrue(connection.BodyWriteFailed, "The request body write must have failed.");
                Assert.AreEqual(1, server.AcceptCount, "A non-idempotent request must not be replayed.");
            });
        }

        [Test]
        public void BodyWriteFailure_NoFinalResponse_SendFailureSurfaces()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                var connection = await InjectIdleConnectionAsync(pool, server.Port, tcp => new FailBodyWriteStream(tcp));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await client.Post(server.Url("/upload"))
                        .WithBody(new byte[64 * 1024])
                        .SendBufferedAsync();
                });

                Assert.IsTrue(connection.BodyWriteFailed, "The request body write must have failed.");
                Assert.IsNotNull(FindInChain<SocketException>(ex),
                    "The original send failure must surface, not the failed response read.");
                Assert.AreEqual(1, server.AcceptCount, "A non-idempotent request must not be replayed.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BodySourceFailure_ServerStillWaiting_FailsWithoutReadingAResponse(bool socketCause)
        {
            AssertAsync.Run(async () =>
            {
                var headRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                    headRead.TrySetResult(true);
                }, keepFirstConnectionOpen: true);

                using var body = new FailAfterReadStream(Encoding.ASCII.GetBytes("abcdefghij"), 5, socketCause);
                using var client = CreateClient(new RawSocketTransport());

                // The server never answers and keeps the connection open: reading a response here
                // would wait for the request timeout, which the test guard is far below.
                var send = client.Post(server.Url("/upload"))
                    .WithStreamBody(body, body.Length, leaveOpen: true)
                    .WithTimeout(TimeSpan.FromMinutes(5))
                    .SendBufferedAsync()
                    .AsTask();

                Assert.AreSame(send, await Task.WhenAny(send, Task.Delay(Guard)),
                    "A body source failure must surface without waiting for a response.");
                Assert.IsTrue(send.IsFaulted, "The request must fail.");
                Assert.IsInstanceOf<UHttpException>(send.Exception.InnerException);
                await WithGuard(headRead.Task);
            });
        }

        /// <summary>Every connection answers 200 without a Connection field and serves further requests.</summary>
        private static LoopbackServer CreateAlwaysKeepAliveServer()
        {
            return new LoopbackServer(async (stream, index) =>
            {
                while (await TryReadHeadAsync(stream) != null)
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            });
        }

        private static T FindInChain<T>(Exception ex) where T : Exception
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is T match)
                    return match;
            }

            return null;
        }

        /// <summary>
        /// Passes the request head through to the socket, then fails every body write the way a
        /// connection reset by the server does (an <see cref="IOException"/> wrapping a
        /// <see cref="SocketException"/>). Reads pass through, so a response the server already
        /// sent stays readable; on platforms where an RST discards the receive buffer this is the
        /// best case the transport can rely on.
        /// </summary>
        private sealed class FailBodyWriteStream : Stream
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _inner;
            private int _headTerminatorMatched;
            private bool _headWritten;
            private volatile bool _bodyWriteFailed;

            public FailBodyWriteStream(TcpClient tcp)
            {
                _tcp = tcp;
                _inner = tcp.GetStream();
            }

            public bool BodyWriteFailed => _bodyWriteFailed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                int headBytes = TakeHeadBytes(buffer, offset, count);
                if (headBytes > 0)
                    _inner.Write(buffer, offset, headBytes);
                if (headBytes < count)
                    throw Reset();
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int headBytes = TakeHeadBytes(buffer, offset, count);
                if (headBytes > 0)
                    await _inner.WriteAsync(buffer, offset, headBytes, cancellationToken).ConfigureAwait(false);
                if (headBytes < count)
                    throw Reset();
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var copy = buffer.ToArray();
                return new ValueTask(WriteAsync(copy, 0, copy.Length, cancellationToken));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _tcp.Close();
                base.Dispose(disposing);
            }

            /// <summary>Number of leading bytes of this write that still belong to the request head.</summary>
            private int TakeHeadBytes(byte[] buffer, int offset, int count)
            {
                if (_headWritten)
                    return 0;

                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[offset + i];
                    bool expectCr = _headTerminatorMatched % 2 == 0;
                    if ((expectCr && b == (byte)'\r') || (!expectCr && b == (byte)'\n'))
                        _headTerminatorMatched++;
                    else
                        _headTerminatorMatched = b == (byte)'\r' ? 1 : 0;

                    if (_headTerminatorMatched == 4)
                    {
                        _headWritten = true;
                        return i + 1;
                    }
                }

                return count;
            }

            private Exception Reset()
            {
                _bodyWriteFailed = true;
                return new IOException(
                    "Unable to write data to the transport connection: Connection reset by peer.",
                    new SocketException((int)SocketError.ConnectionReset));
            }
        }
    }
}
