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

            // 4 settings x 6 bytes each = 24 bytes
            Assert.AreEqual(24, payload.Length);

            // Third setting: InitialWindowSize (0x0004) = 65535
            int initialWindowOffset = 12;
            var initialWindowId = (Http2SettingId)((payload[initialWindowOffset] << 8) | payload[initialWindowOffset + 1]);
            uint initialWindowValue = ((uint)payload[initialWindowOffset + 2] << 24) | ((uint)payload[initialWindowOffset + 3] << 16) |
                                      ((uint)payload[initialWindowOffset + 4] << 8) | payload[initialWindowOffset + 5];

            Assert.AreEqual(Http2SettingId.InitialWindowSize, initialWindowId);
            Assert.AreEqual(65535u, initialWindowValue);

            // Fourth setting: MaxHeaderListSize (0x0006) = 65536
            int maxHeaderListOffset = 18;
            var maxHeaderListId = (Http2SettingId)((payload[maxHeaderListOffset] << 8) | payload[maxHeaderListOffset + 1]);
            uint maxHeaderListValue = ((uint)payload[maxHeaderListOffset + 2] << 24) | ((uint)payload[maxHeaderListOffset + 3] << 16) |
                                      ((uint)payload[maxHeaderListOffset + 4] << 8) | payload[maxHeaderListOffset + 5];

            Assert.AreEqual(Http2SettingId.MaxHeaderListSize, maxHeaderListId);
            Assert.AreEqual(64u * 1024u, maxHeaderListValue);
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
