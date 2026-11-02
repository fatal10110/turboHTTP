using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Tools.Fuzz
{
    /// <summary>
    /// The server side of the h2-connection target. Reads return a fixed server preface (an empty
    /// SETTINGS frame and a SETTINGS ACK), then wait until the client has written its first HEADERS
    /// frame (stream 1 is open), then return the fuzz input, then EOF. Writes are parsed only far
    /// enough to spot that HEADERS frame and are otherwise discarded.
    /// </summary>
    /// <remarks>
    /// One reader (the connection's read loop) touches the read position; writes arrive under the
    /// connection's write lock but from different threads, so the write tracker takes its own lock.
    /// </remarks>
    internal sealed class H2FuzzStream : Stream
    {
        private static readonly byte[] ServerPreface =
        {
            0, 0, 0, 0x04, 0x00, 0, 0, 0, 0, // SETTINGS, no parameters
            0, 0, 0, 0x04, 0x01, 0, 0, 0, 0, // SETTINGS ACK
        };

        private const int ClientPrefaceLength = 24;
        private const byte HeadersFrameType = 0x01;

        private readonly byte[] _input;
        private readonly TaskCompletionSource<bool> _gate =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeLock = new object();
        private readonly byte[] _frameHeader = new byte[9];
        private int _prefacePos;
        private int _inputPos;

        // Write tracker, under _writeLock.
        private int _clientPrefaceRemaining = ClientPrefaceLength;
        private int _frameHeaderFill;
        private int _payloadRemaining;

        public H2FuzzStream(byte[] input)
        {
            _input = input;
        }

        public void OpenGate() => _gate.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
                return 0;

            if (_prefacePos < ServerPreface.Length)
            {
                int n = Math.Min(buffer.Length, ServerPreface.Length - _prefacePos);
                ServerPreface.AsSpan(_prefacePos, n).CopyTo(buffer.Span);
                _prefacePos += n;
                return n;
            }

            if (!_gate.Task.IsCompleted)
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            int count = Math.Min(buffer.Length, _input.Length - _inputPos);
            if (count <= 0)
                return 0;
            _input.AsSpan(_inputPos, count).CopyTo(buffer.Span);
            _inputPos += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(new Memory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            bool sawHeaders = false;
            lock (_writeLock)
            {
                while (buffer.Length > 0)
                {
                    if (_clientPrefaceRemaining > 0)
                    {
                        int skip = Math.Min(_clientPrefaceRemaining, buffer.Length);
                        _clientPrefaceRemaining -= skip;
                        buffer = buffer.Slice(skip);
                        continue;
                    }

                    if (_payloadRemaining > 0)
                    {
                        int skip = Math.Min(_payloadRemaining, buffer.Length);
                        _payloadRemaining -= skip;
                        buffer = buffer.Slice(skip);
                        continue;
                    }

                    int take = Math.Min(_frameHeader.Length - _frameHeaderFill, buffer.Length);
                    buffer.Slice(0, take).CopyTo(_frameHeader.AsSpan(_frameHeaderFill));
                    _frameHeaderFill += take;
                    buffer = buffer.Slice(take);
                    if (_frameHeaderFill < _frameHeader.Length)
                        continue;

                    _frameHeaderFill = 0;
                    _payloadRemaining = (_frameHeader[0] << 16) | (_frameHeader[1] << 8) | _frameHeader[2];
                    if (_frameHeader[3] == HeadersFrameType)
                        sawHeaders = true;
                }
            }

            if (sawHeaders)
                OpenGate();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(new ReadOnlySpan<byte>(buffer, offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return default;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Takes the response the way a streaming consumer does: the body is read to the end (then its
    /// trailers) on the thread pool, away from the read loop that delivers it.
    /// </summary>
    internal sealed class FuzzHttpHandler : IHttpHandler
    {
        public Task BodyTask { get; private set; }
        public UHttpException Error { get; private set; }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
        }

        public ValueTask OnResponseStartAsync(int statusCode, HttpHeaders headers, IResponseBodySource body, RequestContext context)
        {
            if (body != null)
                BodyTask = Task.Run(() => ConsumeAsync(body));
            return default;
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            Error = error;
        }

        private static async Task ConsumeAsync(IResponseBodySource body)
        {
            await using (body)
            {
                var buffer = new byte[4096];
                while (await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) > 0)
                {
                }

                await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
