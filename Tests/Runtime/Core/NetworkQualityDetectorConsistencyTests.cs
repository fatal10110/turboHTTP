using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #185: every detector field is owned by one lock, so a snapshot taken while samples are
    /// added never mixes metrics from two different samples.
    /// </summary>
    [TestFixture]
    public class NetworkQualityDetectorConsistencyTests
    {
        private static readonly NetworkQualitySample Fast = new NetworkQualitySample(50, 50, false, false, 0, true);
        private static readonly NetworkQualitySample TimedOut = new NetworkQualitySample(5000, 5000, true, true, 0, false);

        [Test]
        public void Snapshot_DuringConcurrentAddSample_IsAlwaysOneCompleteSampleState()
        {
            Task.Run(async () =>
            {
                // Window 1 and alpha 1: the metrics equal the last sample exactly.
                var detector = new NetworkQualityDetector(windowSize: 1, promoteAfterConsecutiveWindows: 1, ewmaAlpha: 1d);
                const int writes = 20000;
                var writerDone = 0;

                var writer = Task.Run(() =>
                {
                    for (int i = 0; i < writes; i++)
                        detector.AddSample((i & 1) == 0 ? Fast : TimedOut);
                    Volatile.Write(ref writerDone, 1);
                });

                var reader = Task.Run(() =>
                {
                    int reads = 0;
                    while (Volatile.Read(ref writerDone) == 0 || reads == 0)
                    {
                        var s = detector.GetSnapshot();
                        reads++;
                        if (s.SampleCount == 0)
                            continue;

                        bool fast = s.EwmaLatencyMs == 50d && s.TimeoutRatio == 0d && s.SuccessRatio == 1d;
                        bool timedOut = s.EwmaLatencyMs == 5000d && s.TimeoutRatio == 1d && s.SuccessRatio == 0d;
                        Assert.That(fast || timedOut, Is.True,
                            $"Torn snapshot: latency={s.EwmaLatencyMs} timeout={s.TimeoutRatio} success={s.SuccessRatio}");
                        Assert.That(s.SampleCount, Is.EqualTo(1));

                        // A timed-out sample demotes to Poor in the same AddSample call.
                        if (timedOut)
                            Assert.That(s.Quality, Is.EqualTo(NetworkQuality.Poor));
                    }
                });

                await Task.WhenAll(writer, reader);
            }).GetAwaiter().GetResult();
        }
    }
}
