using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #633: HTTP/1.1 request writes must not trust the platform to end a pending send once the token
    /// fired (Mono and IL2CPP may not, #293/#384). The serializer must end as soon as the token
    /// fires, keep the send buffer the abandoned write still reads out of the pool, observe the
    /// write's later fault and dispose the stream once it ends. The stream here models the worst
    /// case: one write ignores both its token and Dispose.
    /// </summary>
    [TestFixture]
    public class RequestWriteCancellationTests
    {
        // Guard only: cancellation must end the call long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void Serialize_HeadWriteIgnoresTokenAndDispose_CancellationEndsTheSerialize()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckWriteStream(stuckWrite: 1);
                var writeState = new Http11RequestWriteState();
                using var cts = new CancellationTokenSource();
                long abandonedBefore = ByteArrayPool.GetDiagnostics().AbandonCount;
                var send = Http11RequestSerializer.SerializeAsync(Get(), stream, cts.Token, writeState);

                await stream.WritePending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(send).ConfigureAwait(false));
                Assert.IsFalse(writeState.HasWrittenCompleteMessage);
                Assert.Greater(ByteArrayPool.GetDiagnostics().AbandonCount, abandonedBefore,
                    "the header buffer the abandoned write still reads must not go back to the pool");

                // The abandoned write ends later; its observer disposes the unusable stream.
                Assert.IsFalse(stream.IsDisposed, "the stream is disposed only once the abandoned write ends");
                stream.EndWrite();
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void Serialize_KnownLengthBodyWriteIgnoresToken_BodyBufferStaysOutOfThePool()
        {
            AssertAsync.Run(async () =>
            {
                // Write 1 is the head (completes), write 2 the first body chunk (stuck).
                var stream = new StuckWriteStream(stuckWrite: 2);
                using var cts = new CancellationTokenSource();
                var body = Encoding.ASCII.GetBytes("stream-body");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)), body.Length);
                long abandonedBefore = ByteArrayPool.GetDiagnostics().AbandonCount;
                var send = Http11RequestSerializer.SerializeAsync(request, stream, cts.Token, new Http11RequestWriteState());

                await stream.WritePending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(send).ConfigureAwait(false));
                Assert.Greater(ByteArrayPool.GetDiagnostics().AbandonCount, abandonedBefore,
                    "the body buffer the abandoned write still reads must not go back to the pool");
                stream.EndWrite();
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void Serialize_ChunkedBodyWriteIgnoresToken_CancellationEndsTheSerialize()
        {
            AssertAsync.Run(async () =>
            {
                // Write 1 is the head (completes), write 2 the first chunk-size line (stuck).
                var stream = new StuckWriteStream(stuckWrite: 2);
                using var cts = new CancellationTokenSource();
                var body = Encoding.ASCII.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)));
                long abandonedBefore = ByteArrayPool.GetDiagnostics().AbandonCount;
                var send = Http11RequestSerializer.SerializeAsync(request, stream, cts.Token);

                await stream.WritePending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(send).ConfigureAwait(false));
                Assert.GreaterOrEqual(ByteArrayPool.GetDiagnostics().AbandonCount, abandonedBefore + 2,
                    "neither chunk buffer may go back to the pool while the abandoned write runs");
                stream.EndWrite();
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void Serialize_LeasedBufferedBodyWriteIgnoresToken_OwnerReleasedOnlyAfterTheWriteEnds()
        {
            AssertAsync.Run(async () =>
            {
                // Above the small-body threshold (32 KiB), so the body memory itself is written:
                // write 1 is the head (completes), write 2 the leased body memory (stuck).
                var stream = new StuckWriteStream(stuckWrite: 2);
                using var cts = new CancellationTokenSource();
                var owner = new TrackingMemoryOwner(64 * 1024);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithLeasedBody(owner, owner.Memory.Length);
                var send = Http11RequestSerializer.SerializeAsync(request, stream, cts.Token, new Http11RequestWriteState());

                await stream.WritePending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(send).ConfigureAwait(false));

                // The request owner disposes the request after the failed send; the abandoned
                // write still reads the leased memory, so the owner must not go back to its pool.
                request.Dispose();
                Assert.AreEqual(0, owner.DisposeCount,
                    "the leased body memory the abandoned write still reads must not be released");

                stream.EndWrite();
                Assert.AreEqual(1, owner.DisposeCount, "the owner is released once the abandoned write ends");
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void Serialize_LeasedBufferedBodyCompletes_DisposeReleasesTheOwnerAtOnce()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckWriteStream(stuckWrite: 2);
                using var cts = new CancellationTokenSource();
                var owner = new TrackingMemoryOwner(64 * 1024);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithLeasedBody(owner, owner.Memory.Length);
                var send = Http11RequestSerializer.SerializeAsync(request, stream, cts.Token, new Http11RequestWriteState());

                await stream.WritePending.ConfigureAwait(false);
                stream.EndWrite();
                await send.ConfigureAwait(false);

                request.Dispose();
                Assert.AreEqual(1, owner.DisposeCount);
            }, Guard);
        }

        [Test]
        public void Serialize_AbandonedWriteFaultsLater_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new ObjectDisposedException("abandoned-request-write");

            Exception failure = CancelThenFaultWrite(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned request write faulted after cancellation and nobody observed it.");
        }

        [Test]
        public void Serialize_NotCancelled_PendingWriteCompletesNormally()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckWriteStream(stuckWrite: 1);
                var writeState = new Http11RequestWriteState();
                using var cts = new CancellationTokenSource();
                var send = Http11RequestSerializer.SerializeAsync(Get(), stream, cts.Token, writeState);

                await stream.WritePending.ConfigureAwait(false);
                stream.EndWrite();
                await send.ConfigureAwait(false);

                Assert.IsTrue(writeState.HasWrittenCompleteMessage);
                Assert.IsFalse(stream.IsDisposed);
                StringAssert.StartsWith("GET / HTTP/1.1\r\n", stream.WrittenAscii);
            }, Guard);
        }

        private static UHttpRequest Get() => new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelThenFaultWrite(Exception marker)
        {
            var stream = new StuckWriteStream(stuckWrite: 1);
            using var cts = new CancellationTokenSource();
            var send = Http11RequestSerializer.SerializeAsync(Get(), stream, cts.Token);

            cts.Cancel();
            Exception failure = null;
            AssertAsync.Run(async () => failure = await CaptureAsync(send).ConfigureAwait(false), Guard);

            // The platform ends the abandoned write late, with a fault.
            stream.FailWrite(marker);
            return failure;
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
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

        private sealed class TrackingMemoryOwner : IMemoryOwner<byte>
        {
            private int _disposeCount;

            public TrackingMemoryOwner(int length) => Memory = new byte[length];

            public Memory<byte> Memory { get; }
            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Dispose() => Interlocked.Increment(ref _disposeCount);
        }

        /// <summary>
        /// A write-only stream whose write number <c>stuckWrite</c> (1-based) ignores its token and
        /// <see cref="Stream.Dispose()"/>: it ends only when the test calls <see cref="EndWrite"/> or
        /// <see cref="FailWrite"/>. Every other write completes synchronously.
        /// </summary>
        private sealed class StuckWriteStream : Stream
        {
            // Inline continuations: EndWrite/FailWrite return after the observer ran.
            private readonly TaskCompletionSource<bool> _write = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _writePending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly MemoryStream _written = new MemoryStream();
            private readonly int _stuckWrite;
            private byte[] _stuckData;
            private int _disposed;
            private int _writeCount;

            public StuckWriteStream(int stuckWrite) => _stuckWrite = stuckWrite;

            public Task WritePending => _writePending.Task;
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public string WrittenAscii
            {
                get { lock (_written) return Encoding.ASCII.GetString(_written.ToArray()); }
            }

            public void EndWrite()
            {
                lock (_written) _written.Write(_stuckData, 0, _stuckData.Length);
                _write.TrySetResult(true);
            }

            public void FailWrite(Exception ex) => _write.TrySetException(ex);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref _writeCount) != _stuckWrite)
                {
                    lock (_written) _written.Write(buffer.ToArray(), 0, buffer.Length);
                    return default;
                }

                _stuckData = buffer.ToArray();
                _writePending.TrySetResult(true);
                return new ValueTask(_write.Task);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            protected override void Dispose(bool disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
                base.Dispose(disposing);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
