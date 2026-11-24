using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.Transport.Http2.Helpers;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// The in-memory duplex stream under the HTTP/2 connection tests must not hold a thread-pool thread per
    /// pending read: every connection a test leaves reading would keep one, and on the IL2CPP player
    /// (4 minimum workers, slow thread injection) they starved the pool for minutes, timing out unrelated
    /// loopback tests (#1297, found with the device run's heartbeat and a native thread dump).
    /// </summary>
    [TestFixture]
    public class TestDuplexStreamThreadPoolTests
    {
        private const int PendingReads = 16;

        private static int BusyWorkers()
        {
            ThreadPool.GetAvailableThreads(out int available, out _);
            ThreadPool.GetMaxThreads(out int max, out _);
            return max - available;
        }

        [Test]
        public void PendingReadAsync_HoldsNoThreadPoolThread()
        {
            var streams = new BlockingStream[PendingReads];
            var reads = new Task<int>[PendingReads];
            var probe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int busyBefore = BusyWorkers();
            // The reads start on a dedicated thread, not a pool worker, so any work item they queue goes to the
            // pool's global FIFO queue, ahead of the probe: when the probe runs, each of those items has taken a
            // worker, and a read that parks its worker still holds it.
            var starter = new Thread(() =>
            {
                for (int i = 0; i < PendingReads; i++)
                {
                    streams[i] = new BlockingStream();
                    reads[i] = streams[i].ReadAsync(new byte[8], 0, 8, CancellationToken.None);
                }
                ThreadPool.QueueUserWorkItem(_ => probe.TrySetResult(BusyWorkers()));
            })
            { IsBackground = true, Name = "DuplexReadStarter" };
            starter.Start();
            try
            {
                AssertAsync.Run(async () =>
                {
                    int busyAtProbe = await probe.Task.ConfigureAwait(false);
                    Assert.That(busyAtProbe - busyBefore, Is.LessThan(PendingReads / 2),
                        "pending reads hold thread-pool workers");
                    for (int i = 0; i < PendingReads; i++)
                        streams[i].Write(new[] { (byte)i }, 0, 1);
                    for (int i = 0; i < PendingReads; i++)
                        Assert.AreEqual(1, await reads[i].ConfigureAwait(false));
                }, TimeSpan.FromSeconds(60));
            }
            finally
            {
                foreach (var stream in streams)
                    stream?.CompleteWriting();
            }
        }

        [Test]
        public void PendingReadAsync_EndsOnCompleteWriting_AndEveryLaterReadEnds()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new BlockingStream();
                stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
                var buffer = new byte[2];
                Assert.AreEqual(2, await stream.ReadAsync(buffer, 0, 2, CancellationToken.None));
                Assert.AreEqual(1, await stream.ReadAsync(buffer, 0, 2, CancellationToken.None));
                var pending = stream.ReadAsync(buffer, 0, 2, CancellationToken.None);
                stream.CompleteWriting();
                Assert.AreEqual(0, await pending);
                Assert.AreEqual(0, await stream.ReadAsync(buffer, 0, 2, CancellationToken.None));
                Assert.AreEqual(0, stream.Read(buffer, 0, 2));
                Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[] { 4 }, 0, 1));
            }, TimeSpan.FromSeconds(30));
        }

        [Test]
        public void PendingReadAsync_IsCanceledByItsToken()
        {
            AssertAsync.Run(async () =>
            {
                var stream = new BlockingStream();
                using var cts = new CancellationTokenSource();
                var pending = stream.ReadAsync(new byte[4], 0, 4, cts.Token);
                cts.Cancel();
                try
                {
                    await pending;
                    Assert.Fail("the canceled read completed");
                }
                catch (OperationCanceledException)
                {
                }
                stream.Write(new byte[] { 9 }, 0, 1);
                var buffer = new byte[4];
                Assert.AreEqual(1, await stream.ReadAsync(buffer, 0, 4, CancellationToken.None));
                Assert.AreEqual(9, buffer[0]);
            }, TimeSpan.FromSeconds(30));
        }
    }
}
