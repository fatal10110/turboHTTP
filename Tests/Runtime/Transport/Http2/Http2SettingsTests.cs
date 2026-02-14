using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2SettingsTests
    {
        [Test]
        public void Default_MaxHeaderListSize_Is64KB()
        {
            var settings = new Http2Settings();
            Assert.AreEqual(64 * 1024, settings.MaxHeaderListSize);
        }

        [Test]
        public void SerializeClientSettings_IncludesMaxHeaderListSize()
        {
            var settings = new Http2Settings();
            var payload = settings.SerializeClientSettings();

            // 3 settings x 6 bytes each = 18 bytes
            Assert.AreEqual(18, payload.Length);

            // Third setting: MaxHeaderListSize (0x0006) = 65536
            int offset = 12; // 3rd entry
            var id = (Http2SettingId)((payload[offset] << 8) | payload[offset + 1]);
            uint value = ((uint)payload[offset + 2] << 24) | ((uint)payload[offset + 3] << 16) |
                         ((uint)payload[offset + 4] << 8) | payload[offset + 5];

            Assert.AreEqual(Http2SettingId.MaxHeaderListSize, id);
            Assert.AreEqual(64u * 1024u, value);
        }

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
