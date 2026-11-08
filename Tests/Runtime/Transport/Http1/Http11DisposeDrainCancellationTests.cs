using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// Phase 22a.6 Step 1, HTTP/1.1 item 23 (#102): the drain an early dispose attempts reads under a token
    /// linked to the request's transport token and bounded by the 2 s drain timeout. A cancelled request
    /// stops the drain at once; without it, the drain still carries a cancellable (timeout) token; either way
    /// a failed drain closes the body instead of handing the connection back for reuse (RFC 9112 Section 9.6).
    /// </summary>
    [TestFixture]
    public sealed class Http11DisposeDrainCancellationTests
    {
        [Test]
        public void DisposeDrain_CancelledTransportToken_StopsTheDrainAndClosesTheBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var stream = new ObservingStream();

                await DisposeUnreadBodyAsync(stream, cts.Token, async source =>
                {
                    Assert.IsTrue(stream.Reads > 0, "the dispose drain did not read");
                    Assert.IsTrue(stream.SawCancelledToken, "the drain token is not linked to the request's transport token");
                    AssertAsync.ThrowsAsync<ObjectDisposedException>(async () => await source.ReadAsync(new byte[1], CancellationToken.None));
                });
            });
        }

        [Test]
        public void DisposeDrain_WithoutRequestToken_ReadsUnderTheDrainTimeoutToken()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new ObservingStream();

                await DisposeUnreadBodyAsync(stream, CancellationToken.None, async source =>
                {
                    Assert.IsTrue(stream.Reads > 0, "the dispose drain did not read");
                    Assert.IsTrue(stream.AllTokensCancellable, "the drain read without the 2 s timeout token");
                    AssertAsync.ThrowsAsync<ObjectDisposedException>(async () => await source.ReadAsync(new byte[1], CancellationToken.None));
                });
            });
        }

        private static async Task DisposeUnreadBodyAsync(
            ObservingStream stream,
            CancellationToken transportToken,
            Func<Http11ResponseBodySource, Task> assertAfterDispose)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = true, // reusable framing, small remainder: the dispose drain is attempted
                BodyKind = Http11ResponseBodyKind.ContentLength,
                ContentLength = 100
            };
            var lease = new ConnectionLease(null, new SemaphoreSlim(0, 1), new PooledConnection(socket, stream, "drain.local", 80, false));
            var source = new Http11ResponseBodySource(head, lease, transportToken, TimeSpan.FromSeconds(30));
            head.Dispose();

            await source.DisposeAsync();
            await assertAfterDispose(source);
        }

        /// <summary>
        /// Records the token of every read. A cancelled token ends the read with OperationCanceledException;
        /// otherwise the read fails as a stalled-then-reset peer would, so the test never waits on a timer.
        /// </summary>
        private sealed class ObservingStream : Stream
        {
            internal int Reads { get; private set; }
            internal bool SawCancelledToken { get; private set; }
            internal bool AllTokensCancellable { get; private set; } = true;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return new ValueTask<int>(Observe(cancellationToken));
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Observe(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("synchronous read not expected");
            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private Task<int> Observe(CancellationToken token)
            {
                Reads++;
                AllTokensCancellable &= token.CanBeCanceled;
                if (token.IsCancellationRequested)
                {
                    SawCancelledToken = true;
                    return Task.FromCanceled<int>(token);
                }

                return Task.FromException<int>(new IOException("peer stalled and reset"));
            }
        }
    }
}
