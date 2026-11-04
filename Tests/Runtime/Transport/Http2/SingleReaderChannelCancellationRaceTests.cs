using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #21: a write or completion racing the cancellation of a pending read must never deadlock. The
    /// read loop writes response DATA into this channel while request cancellation cancels the
    /// consumer's pending read; disposing the read's registration inside the channel lock used to
    /// wait for the running cancellation callback, which itself waited for that lock, stalling the
    /// whole connection.
    /// </summary>
    [TestFixture]
    public class SingleReaderChannelCancellationRaceTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void PendingReadCancellation_RacingWriteOrComplete_NeverDeadlocks(bool complete)
        {
            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(30000);
                for (int i = 0; i < 2000; i++)
                {
                    var channel = new SingleReaderChannel<int>(4);
                    using var readCts = new CancellationTokenSource();
                    var read = channel.ReadAsync(readCts.Token).AsTask();

                    using var start = new Barrier(2);
                    var canceller = Task.Run(() =>
                    {
                        start.SignalAndWait();
                        readCts.Cancel();
                    });
                    var writer = Task.Run(() =>
                    {
                        start.SignalAndWait();
                        if (complete)
                            channel.Complete();
                        else
                            channel.TryWrite(i);
                    });

                    var both = Task.WhenAll(canceller, writer);
                    if (await Task.WhenAny(both, Task.Delay(Timeout.Infinite, watchdog.Token)) != both)
                        Assert.Fail($"write/cancel race deadlocked at iteration {i}");
                    await both;

                    // The read ends one way or the other: the value, cancellation or completion.
                    try
                    {
                        await read;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (InvalidOperationException) when (complete)
                    {
                    }
                }
            });
        }
    }
}
