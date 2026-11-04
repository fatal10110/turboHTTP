using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #344: on IL2CPP a pending read is sometimes not ended by the disposal of its stream, so the
    /// BouncyCastle handshake deadline (#142) and caller cancellation must not wait for the
    /// handshake to notice the dispose. The stream here models the worst case: its read ignores
    /// both its token and <see cref="Stream.Dispose()"/>.
    /// </summary>
    [TestFixture]
    public class BouncyCastleHandshakeAbandonTests
    {
        // Guard only: the handshake bound or the cancel must end the call long before this; it
        // never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
        private static readonly string[] Http11 = { "http/1.1" };

        [Test]
        public void Deadline_ReadIgnoresTokenAndDispose_FailsAtTheBound()
        {
            const int timeoutMillis = 300;
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(timeoutMillis);
            var stream = new StuckHandshakeStream();
            try
            {
                AssertAsync.Run(async () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var wrap = provider.WrapAsync(stream, "localhost", Http11, CancellationToken.None);
                    var failure = await CaptureAsync(wrap).ConfigureAwait(false);
                    stopwatch.Stop();

                    Assert.IsInstanceOf<IOException>(failure, failure?.ToString());
                    Assert.IsInstanceOf<TimeoutException>(failure.InnerException, failure.ToString());
                    StringAssert.Contains("timed out after 300 ms", failure.Message);
                    Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, timeoutMillis - 50,
                        "The handshake failed before its bound.");
                    Assert.Less(stopwatch.ElapsedMilliseconds, 10000,
                        "The handshake must fail at its bound although the read never ends.");
                    Assert.IsTrue(stream.IsDisposed, "The deadline disposes the inner stream.");

                    // The abandoned handshake still reaches its (stuck) read; it is never restarted.
                    await stream.ReadPending.ConfigureAwait(false);
                    Assert.IsFalse(stream.ReadEnded, "The provider returned without waiting for the read.");
                    Assert.AreEqual(1, stream.ReadCalls, "One handshake read per stream.");
                    Assert.AreEqual(1, stream.ClientHellos, "One ClientHello per stream.");
                }, Guard);
            }
            finally
            {
                stream.EndRead(0);
            }
        }

        [Test]
        public void CallerCancel_ReadIgnoresTokenAndDispose_ThrowsOperationCanceled()
        {
            // The handshake bound is far away: only the caller's token can end the call in time.
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(30000);
            var stream = new StuckHandshakeStream();
            try
            {
                AssertAsync.Run(async () =>
                {
                    using var cts = new CancellationTokenSource();
                    var wrap = provider.WrapAsync(stream, "localhost", Http11, cts.Token);

                    await stream.ReadPending.ConfigureAwait(false);
                    cts.Cancel();
                    var failure = await CaptureAsync(wrap).ConfigureAwait(false);

                    Assert.IsInstanceOf<OperationCanceledException>(failure, failure?.ToString());
                    Assert.IsFalse(stream.ReadEnded, "The provider returned without waiting for the read.");
                    Assert.IsTrue(stream.IsDisposed, "Cancellation disposes the inner stream.");
                    Assert.AreEqual(1, stream.ClientHellos, "One ClientHello per stream.");
                }, Guard);
            }
            finally
            {
                stream.EndRead(0);
            }
        }

        [Test]
        public void AbandonedHandshake_LateReadFault_IsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new IOException("late read failure after the handshake was abandoned");

            var failure = CancelHandshakeThenFailRead(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure, failure?.ToString());
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned handshake faulted after WrapAsync returned and nobody observed it.");
        }

        // Not inlined: no reference to the abandoned handshake task may stay on the test's stack
        // while the probe collects.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelHandshakeThenFailRead(Exception marker)
        {
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(30000);
            var stream = new StuckHandshakeStream();
            Exception failure = null;
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var wrap = provider.WrapAsync(stream, "localhost", Http11, cts.Token);
                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();
                failure = await CaptureAsync(wrap).ConfigureAwait(false);
            }, Guard);

            // The read finally fails. The handshake aborts (disposing the stream once more) and its
            // fault reaches the provider's observer, which disposes the stream again after reading
            // the fault: two disposals after the read ended mean the abandoned task has ended.
            stream.FailRead(marker);
            Assert.IsTrue(
                SpinWait.SpinUntil(() => stream.DisposeCallsAfterReadEnded >= 2, Guard),
                "The abandoned handshake did not end after its read failed.");
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
        /// An inner transport stream: writes succeed (and count ClientHello records); the first
        /// read ignores its token and <see cref="Stream.Dispose()"/> and ends only when the test
        /// calls <see cref="EndRead(int)"/> or <see cref="FailRead"/>.
        /// </summary>
        private sealed class StuckHandshakeStream : Stream
        {
            // Inline continuations: EndRead/FailRead usually return after the handshake ended.
            private readonly TaskCompletionSource<int> _read = new TaskCompletionSource<int>();
            private readonly TaskCompletionSource<bool> _readPending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;
            private int _readEnded;
            private int _readCalls;
            private int _clientHellos;
            private int _disposeCallsAfterReadEnded;

            public Task ReadPending => _readPending.Task;
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
            public bool ReadEnded => Volatile.Read(ref _readEnded) != 0;
            public int ReadCalls => Volatile.Read(ref _readCalls);
            public int ClientHellos => Volatile.Read(ref _clientHellos);
            public int DisposeCallsAfterReadEnded => Volatile.Read(ref _disposeCallsAfterReadEnded);

            public void EndRead(int count)
            {
                Interlocked.Exchange(ref _readEnded, 1);
                _read.TrySetResult(count);
            }

            public void FailRead(Exception ex)
            {
                Interlocked.Exchange(ref _readEnded, 1);
                _read.TrySetException(ex);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _readCalls) > 1)
                    return Task.FromResult(0);

                _readPending.TrySetResult(true);
                return _read.Task;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                // A handshake record (22) whose first message is a ClientHello (1).
                if (count > 5 && buffer[offset] == 0x16 && buffer[offset + 5] == 0x01)
                    Interlocked.Increment(ref _clientHellos);
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            protected override void Dispose(bool disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
                if (ReadEnded)
                    Interlocked.Increment(ref _disposeCallsAfterReadEnded);
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
