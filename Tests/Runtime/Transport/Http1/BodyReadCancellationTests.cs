using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #345: once the head is parsed, HTTP/1.1 body reads and the 407 proxy-challenge drain must end
    /// as soon as their token fires even when the platform never ends the pending socket read (Mono,
    /// IL2CPP), keep the abandoned read's buffer out of the pool, never leave a read targeting caller
    /// memory, and observe the read's later fault. <see cref="AbandonedReadStream"/> ignores both the
    /// token and Dispose.
    /// </summary>
    [TestFixture]
    public class BodyReadCancellationTests
    {
        // Guard only: cancellation must end the call long before this; it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void ContentLengthRead_ReadIgnoresTokenAndDispose_CancellationEndsTheRead()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                await using var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100);
                using var cts = new CancellationTokenSource();

                var read = source.ReadAsync(new byte[100], cts.Token).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read).ConfigureAwait(false));
                AssertNotPooled(stream.ReadBuffer);
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ChunkedRead_ChunkSizeReadIgnoresTokenAndDispose_CancellationEndsTheRead()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                await using var source = NewSource(stream, socket, Http11ResponseBodyKind.Chunked, null);
                using var cts = new CancellationTokenSource();

                var read = source.ReadAsync(new byte[100], cts.Token).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read).ConfigureAwait(false));
                AssertNotPooled(stream.ReadBuffer);
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void LargeRead_PendingWithCancellableToken_NeverTargetsCallerMemory()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                await using var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 1 << 20);
                using var cts = new CancellationTokenSource();
                var destination = ArrayPool<byte>.Shared.Rent(16 * 1024);

                var read = source.ReadAsync(destination, cts.Token).AsTask();
                await stream.ReadPending.ConfigureAwait(false);

                // A caller returns its (often pooled) buffer once the read reports cancellation; an
                // abandoned read must not keep writing into it.
                Assert.AreNotSame(destination, stream.ReadBuffer);
                cts.Cancel();

                Assert.IsInstanceOf<OperationCanceledException>(await CaptureAsync(read).ConfigureAwait(false));
                ArrayPool<byte>.Shared.Return(destination);
                AssertNotPooled(stream.ReadBuffer);
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void ContentLengthRead_RequestTimeoutFires_ReportsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                using var timeout = new CancellationTokenSource();
                await using var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100, timeout.Token);

                var read = source.ReadAsync(new byte[100], CancellationToken.None).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                timeout.Cancel();

                var failure = await CaptureAsync(read).ConfigureAwait(false);
                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)failure).HttpError.Type);
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void PendingReads_NotCancelled_CompleteInOrder()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var socket = NewSocket();
                await using var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 8);
                using var cts = new CancellationTokenSource();
                var buffer = new byte[16];

                // Two pending reads in a row reuse the reader's race state.
                var first = source.ReadAsync(buffer, cts.Token).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                stream.EndRead("abcd");
                Assert.AreEqual(4, await first.ConfigureAwait(false));

                var second = source.ReadAsync(buffer.AsMemory(4), cts.Token).AsTask();
                await stream.ReadPending.ConfigureAwait(false);
                stream.EndRead("efgh");
                Assert.AreEqual(4, await second.ConfigureAwait(false));
                Assert.AreEqual("abcdefgh", System.Text.Encoding.ASCII.GetString(buffer, 0, 8));
                Assert.AreEqual(0, await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false));
            }, Guard);
        }

        [Test]
        public void ProxyChallengeDrain_ReadIgnoresTokenAndDispose_CancellationEndsTheDrain()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new AbandonedReadStream();
                using var cts = new CancellationTokenSource();
                var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
                {
                    StatusCode = (HttpStatusCode)407,
                    Headers = HttpHeaders.Empty,
                    KeepAlive = true,
                    BodyKind = Http11ResponseBodyKind.ContentLength,
                    ContentLength = 32 * 1024
                };

                var drain = RawSocketTransport.TryDrainProxyChallengeBodyAsync(head, cts.Token);
                await stream.ReadPending.ConfigureAwait(false);
                cts.Cancel();

                var winner = await Task.WhenAny(drain, Task.Delay(Guard)).ConfigureAwait(false);
                Assert.AreSame(drain, winner, "the 407 drain outlived its cancellation");
                Assert.IsFalse(await drain.ConfigureAwait(false), "a cancelled drain leaves the connection unusable");

                // The drain returned its own buffer; the abandoned read must target neither it nor a
                // reader buffer that went back to the pool.
                head.Dispose();
                AssertNotPooled(stream.ReadBuffer);
                stream.EndRead(0);
            }, Guard);
        }

        [Test]
        public void BodyRead_AbandonedReadFaultsLater_FaultIsObserved()
        {
            using var probe = new UnobservedTaskProbe();
            var marker = new ObjectDisposedException("abandoned-body-read");

            Exception failure = CancelThenFaultBodyRead(marker);
            probe.CollectWithControl();

            if (!probe.ControlReported)
            {
                TestRequirements.IgnoreEnvironment("This runtime did not report a known unobserved faulted task.");
                return;
            }

            Assert.IsInstanceOf<OperationCanceledException>(failure);
            Assert.IsFalse(probe.WasReported(marker),
                "The abandoned body read faulted after cancellation and nobody observed it.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception CancelThenFaultBodyRead(Exception marker)
        {
            var stream = new AbandonedReadStream();
            Exception failure = null;
            using (var socket = NewSocket())
            using (var cts = new CancellationTokenSource())
            {
                var source = NewSource(stream, socket, Http11ResponseBodyKind.ContentLength, 100);
                AssertAsync.Run(async () =>
                {
                    var read = source.ReadAsync(new byte[100], cts.Token).AsTask();
                    await stream.ReadPending.ConfigureAwait(false);
                    cts.Cancel();
                    failure = await CaptureAsync(read).ConfigureAwait(false);
                    await source.DisposeAsync().ConfigureAwait(false);
                }, Guard);
            }

            // The platform ends the abandoned read late, with a fault.
            stream.FailRead(marker);
            return failure;
        }

        private static Socket NewSocket() =>
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private static Http11ResponseBodySource NewSource(
            AbandonedReadStream stream,
            Socket socket,
            Http11ResponseBodyKind kind,
            long? contentLength,
            CancellationToken transportReadToken = default)
        {
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = kind,
                ContentLength = contentLength
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "example.test", 80, false));
            return new Http11ResponseBodySource(head, lease, transportReadToken, TimeSpan.FromSeconds(30));
        }

        // A buffer an abandoned read may still write into must not be handed to the next renter.
        private static void AssertNotPooled(byte[] readBuffer)
        {
            Assert.IsNotNull(readBuffer, "no read was issued");
            var rented = ArrayPool<byte>.Shared.Rent(readBuffer.Length);
            try
            {
                Assert.AreNotSame(readBuffer, rented, "the abandoned read's buffer went back to the pool");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard)).ConfigureAwait(false);
            Assert.AreSame(task, winner, "the read outlived its cancellation");
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
