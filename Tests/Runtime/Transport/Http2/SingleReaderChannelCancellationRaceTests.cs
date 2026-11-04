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
        // Per iteration, not per run: a starved pool (API 26 emulator under memory pressure) only
        // slows the scheduling of each pair; a real deadlock never ends (#330).
        private const int IterationBoundMs = 5000;

        // Busy-wait budget of the start gate: enough for the pair to meet when both run, bounded
        // so a task that runs alone proceeds instead of holding a pool thread.
        private const int StartGateSpins = 20000;

        [TestCase(false)]
        [TestCase(true)]
        public void PendingReadCancellation_RacingWriteOrComplete_NeverDeadlocks(bool complete)
        {
            AssertAsync.Run(async () =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    var channel = new SingleReaderChannel<int>(4);
                    using var readCts = new CancellationTokenSource();
                    var read = channel.ReadAsync(readCts.Token).AsTask();

                    var gate = new StartGate();
                    int value = i;
                    var canceller = Task.Run(() =>
                    {
                        gate.ArriveAndSpin();
                        readCts.Cancel();
                    });
                    var writer = Task.Run(() =>
                    {
                        gate.ArriveAndSpin();
                        if (complete)
                            channel.Complete();
                        else
                            channel.TryWrite(value);
                    });

                    var both = Task.WhenAll(canceller, writer);
                    using (var iterationBound = new CancellationTokenSource())
                    {
                        var timeout = Task.Delay(IterationBoundMs, iterationBound.Token);
                        if (await Task.WhenAny(both, timeout).ConfigureAwait(false) != both)
                            Assert.Fail($"write/cancel race deadlocked at iteration {i} (no progress in {IterationBoundMs} ms)");
                        iterationBound.Cancel();
                    }
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

        /// <summary>
        /// Lines the canceller and the writer up without blocking a pool thread (a Barrier did,
        /// #330): each spins a bounded number of times for the other, then goes ahead.
        /// </summary>
        private sealed class StartGate
        {
            private int _arrived;

            public void ArriveAndSpin()
            {
                Interlocked.Increment(ref _arrived);
                for (int spin = 0; spin < StartGateSpins && Volatile.Read(ref _arrived) < 2; spin++)
                    Thread.SpinWait(8);
            }
        }
    }
}
