using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class NetworkQualityDetectorTests
    {
        [Test]
        public void Ewma_UsesChronologicalOrder_AfterRingWrap()
        {
            var detector = new NetworkQualityDetector(
                windowSize: 3,
                promoteAfterConsecutiveWindows: 1,
                ewmaAlpha: 0.5);

            detector.AddSample(new NetworkQualitySample(100, 100, false, false, 0, true));
            detector.AddSample(new NetworkQualitySample(200, 200, false, false, 0, true));
            detector.AddSample(new NetworkQualitySample(300, 300, false, false, 0, true));
            detector.AddSample(new NetworkQualitySample(1000, 1000, false, false, 0, true));

            // After wrap (window size 3), live chronological samples are 200, 300, 1000.
            // EWMA(alpha=0.5): 200 -> 250 -> 625.
            var snapshot = detector.GetSnapshot();
            Assert.That(snapshot.EwmaLatencyMs, Is.EqualTo(625d).Within(0.001d));
            Assert.AreEqual(3, snapshot.SampleCount);
        }
    }
}
