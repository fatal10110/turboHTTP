using NUnit.Framework;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        private int _streamingLeakCountAtSetUp;

        // Every test of this fixture fails when it drops an undisposed UHttpStreamingResponse (#366).
        [SetUp]
        public void CaptureStreamingResponseLeakCount()
        {
            _streamingLeakCountAtSetUp = StreamingResponseLeakCheck.Begin();
        }

        [TearDown]
        public void AssertNoStreamingResponseLeak()
        {
            StreamingResponseLeakCheck.AssertNoLeakSince(_streamingLeakCountAtSetUp);
        }
    }
}
