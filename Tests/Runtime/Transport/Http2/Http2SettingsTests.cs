using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2SettingsTests
    {
        [Test]
        public void Apply_ClampsUintSettingsToIntMax()
        {
            var settings = new Http2Settings();
            uint tooLarge = (uint)int.MaxValue + 1u;

            settings.Apply(Http2SettingId.HeaderTableSize, tooLarge);
            settings.Apply(Http2SettingId.MaxConcurrentStreams, uint.MaxValue);
            settings.Apply(Http2SettingId.MaxHeaderListSize, tooLarge);

            Assert.AreEqual(int.MaxValue, settings.HeaderTableSize);
            Assert.AreEqual(int.MaxValue, settings.MaxConcurrentStreams);
            Assert.AreEqual(int.MaxValue, settings.MaxHeaderListSize);
        }
    }
}
