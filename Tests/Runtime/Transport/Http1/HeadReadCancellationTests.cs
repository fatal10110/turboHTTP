using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #293: on Unity Mono (and IL2CPP, #322) a pending socket read is sometimes ended neither by its
    /// token nor by the disposal of its socket. The response head read and the CONNECT reply read
    /// must still end as soon as the token fires, keep the abandoned read's buffer out of the pool,
    /// observe its later fault and dispose the stream once it ends. The stream here models the
    /// worst case: its read ignores both the token and Dispose.
    /// </summary>
    [TestFixture]
    public class HeadReadCancellationTests
    {
        // Guard only: cancellation must end the call long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void ParseHead_ReadIgnoresTokenAndDispose_CancellationEndsTheParse()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckReadStream();
                using var cts = new CancellationTokenSource();
                var parse = Http11ResponseParser.ParseHeadAsync(stream, HttpMethod.GET, cts.Token);

                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();

                var failure = await CaptureAsync(parse).ConfigureAwait(false);
                Assert.IsInstanceOf<OperationCanceledException>(failure);

                // The abandoned read ends later; its observer disposes the unusable stream.
                Assert.IsFalse(stream.IsDisposed, "the stream is disposed only once the abandoned read ends");
                stream.EndRead(0);
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void EnsureBuffered_AbandonedRead_BufferIsNotReturnedToThePool()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckReadStream();
                using var cts = new CancellationTokenSource();
                var reader = new Http11ResponseParser.BufferedStreamReader(stream);
                var wait = reader.EnsureBufferedAsync(cts.Token);

                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(wait).ConfigureAwait(false));

                // The read still targets the reader's buffer: a pooled buffer here would be handed
                // to the next renter while the platform can still write into it.
                reader.Dispose();
                var rented = ArrayPool<byte>.Shared.Rent(stream.ReadBuffer.Length);
                try
                {
                    Assert.AreNotSame(stream.ReadBuffer, rented);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void EnsureBuffered_AbandonedReadFaultsLater_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new ObjectDisposedException("abandoned-head-read");

            Exception failure = CancelThenFaultRead(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned head read faulted after cancellation and nobody observed it.");
        }

        [Test]
        public void ConnectReply_ReadIgnoresTokenAndDispose_CancellationEndsTheRead()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckReadStream();
                using var cts = new CancellationTokenSource();
                var read = ConnectReplyReader.ReadAsync(stream, cts.Token);

                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read).ConfigureAwait(false));
                stream.EndRead(0);
                Assert.IsTrue(stream.IsDisposed);
            }, Guard);
        }

        [Test]
        public void ParseHead_NotCancelled_ReadCompletesNormally()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new StuckReadStream();
                using var cts = new CancellationTokenSource();
                var parse = Http11ResponseParser.ParseHeadAsync(stream, HttpMethod.GET, cts.Token);

                await stream.ReadPending.ConfigureAwait(false);
                stream.EndRead("HTTP/1.1 204 No Content\r\n\r\n");

                using var head = await parse.ConfigureAwait(false);
                Assert.AreEqual(204, (int)head.StatusCode);
                Assert.IsFalse(stream.IsDisposed);
            }, Guard);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelThenFaultRead(Exception marker)
        {
            var stream = new StuckReadStream();
            using var cts = new CancellationTokenSource();
            var reader = new Http11ResponseParser.BufferedStreamReader(stream);
            var wait = reader.EnsureBufferedAsync(cts.Token);

            cts.Cancel();
            Exception failure = null;
            AssertAsync.Run(async () => failure = await CaptureAsync(wait).ConfigureAwait(false), Guard);
            reader.Dispose();

            // The platform ends the abandoned read late, with a fault.
            stream.FailRead(marker);
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

        /// <summary>
        /// One read that ignores its token and <see cref="Stream.Dispose()"/>: it ends only when the
        /// test calls <see cref="EndRead(int)"/> or <see cref="FailRead"/>.
        /// </summary>
        private sealed class StuckReadStream : Stream
        {
            // Inline continuations: EndRead/FailRead return after the observer ran.
            private readonly TaskCompletionSource<int> _read = new TaskCompletionSource<int>();
            private readonly TaskCompletionSource<bool> _readPending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;
            private int _readCount;

            public Task ReadPending => _readPending.Task;
            public byte[] ReadBuffer { get; private set; }
            private int ReadOffset { get; set; }
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void EndRead(int count) => _read.TrySetResult(count);

            public void EndRead(string ascii)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(ascii);
                Array.Copy(bytes, 0, ReadBuffer, ReadOffset, bytes.Length);
                _read.TrySetResult(bytes.Length);
            }

            public void FailRead(Exception ex) => _read.TrySetException(ex);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _readCount) > 1)
                    return Task.FromResult(0);

                ReadBuffer = buffer;
                ReadOffset = offset;
                _readPending.TrySetResult(true);
                return _read.Task;
            }

            protected override void Dispose(bool disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
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
