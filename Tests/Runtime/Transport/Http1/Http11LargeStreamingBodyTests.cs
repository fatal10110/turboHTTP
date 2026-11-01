using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #147: streamed HTTP/1.1 bodies have no size cap, whatever their framing. The head parser no
    /// longer rejects a Content-Length above 100 MB, and the body source counts nothing against a cap.
    /// </summary>
    [TestFixture]
    public class Http11LargeStreamingBodyTests
    {
        private const long PayloadBytes = (100L * 1024 * 1024) + 1;

        [TestCase(false)]
        [TestCase(true)]
        public void StreamedBody_MoreThan100MB_ReadsToEndWithoutError(bool chunked)
        {
            AssertAsync.Run(async () =>
            {
                var headText = chunked
                    ? "HTTP/1.1 200 OK\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: " + PayloadBytes + "\r\n\r\n";

                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var stream = new SyntheticResponseStream(Encoding.ASCII.GetBytes(headText), PayloadBytes, chunked);
                var head = await Http11ResponseParser.ParseHeadAsync(stream, HttpMethod.GET, CancellationToken.None);
                Assert.AreEqual(
                    chunked ? Http11ResponseBodyKind.Chunked : Http11ResponseBodyKind.ContentLength,
                    head.BodyKind);

                var lease = new ConnectionLease(
                    null,
                    new SemaphoreSlim(0, 1),
                    new PooledConnection(socket, stream, "example.test", 80, false));

                await using var source = new Http11ResponseBodySource(
                    head,
                    lease,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(60));
                head.Dispose();

                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, CancellationToken.None);
                    if (read == 0)
                        break;
                    total += read;
                }

                Assert.AreEqual(PayloadBytes, total);
            });
        }

        /// <summary>
        /// Produces a response head followed by <c>bodyBytes</c> zero bytes, raw or in 64 KB chunks,
        /// without holding the body in memory.
        /// </summary>
        private sealed class SyntheticResponseStream : Stream
        {
            private const int ChunkBytes = 64 * 1024;
            private readonly byte[] _head;
            private readonly bool _chunked;
            private long _bodyRemaining;
            private int _headOffset;
            private byte[] _pending = Array.Empty<byte>();
            private int _pendingOffset;
            private int _chunkRemaining;
            private bool _finished;

            public SyntheticResponseStream(byte[] head, long bodyBytes, bool chunked)
            {
                _head = head;
                _bodyRemaining = bodyBytes;
                _chunked = chunked;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Read(new Span<byte>(buffer, offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                if (buffer.Length == 0)
                    return 0;

                if (_headOffset < _head.Length)
                    return CopyFrom(_head, ref _headOffset, buffer);

                if (_pendingOffset < _pending.Length)
                    return CopyFrom(_pending, ref _pendingOffset, buffer);

                if (!_chunked)
                    return ReadZeros(buffer, ref _bodyRemaining);

                if (_chunkRemaining > 0)
                {
                    long remaining = _chunkRemaining;
                    int n = ReadZeros(buffer, ref remaining);
                    _chunkRemaining = (int)remaining;
                    if (_chunkRemaining == 0)
                        SetPending("\r\n");
                    return n;
                }

                if (_bodyRemaining > 0)
                {
                    _chunkRemaining = (int)Math.Min(ChunkBytes, _bodyRemaining);
                    _bodyRemaining -= _chunkRemaining;
                    SetPending(_chunkRemaining.ToString("x") + "\r\n");
                    return CopyFrom(_pending, ref _pendingOffset, buffer);
                }

                if (!_finished)
                {
                    _finished = true;
                    SetPending("0\r\n\r\n");
                    return CopyFrom(_pending, ref _pendingOffset, buffer);
                }

                return 0;
            }

            private void SetPending(string text)
            {
                _pending = Encoding.ASCII.GetBytes(text);
                _pendingOffset = 0;
            }

            private static int CopyFrom(byte[] source, ref int offset, Span<byte> destination)
            {
                int n = Math.Min(destination.Length, source.Length - offset);
                source.AsSpan(offset, n).CopyTo(destination);
                offset += n;
                return n;
            }

            private static int ReadZeros(Span<byte> destination, ref long remaining)
            {
                if (remaining <= 0)
                    return 0;

                int n = (int)Math.Min(destination.Length, remaining);
                destination.Slice(0, n).Clear();
                remaining -= n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush()
            {
            }
        }
    }
}
