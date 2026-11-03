using System;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Platform
{
    /// <summary>
    /// #47: pooled connections idle at most 60 s on mobile players and 120 s on desktop.
    /// The Editor compiles the desktop branch; the mobile constant is asserted directly.
    /// Mobile behavior itself is needs-device-validation (#99).
    /// </summary>
    [TestFixture]
    public class PlatformConnectionIdleTimeoutTests
    {
        [Test]
        public void MobileIdleTimeout_IsAtMostSixtySeconds_DesktopIsTwoMinutes()
        {
            Assert.LessOrEqual(PlatformConfig.MobileConnectionIdleTimeoutSeconds, 60);
            Assert.AreEqual(120, PlatformConfig.DesktopConnectionIdleTimeoutSeconds);
        }

        [Test]
        public void RecommendedIdleTimeout_MatchesTheBuildTarget_AndIsThePoolDefault()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            var expected = TimeSpan.FromSeconds(PlatformConfig.MobileConnectionIdleTimeoutSeconds);
#else
            var expected = TimeSpan.FromSeconds(PlatformConfig.DesktopConnectionIdleTimeoutSeconds);
#endif
            Assert.AreEqual(expected, PlatformConfig.RecommendedConnectionIdleTimeout);
            Assert.AreEqual(expected, new ConnectionPoolOptions().ConnectionIdleTimeout);
        }

        [Test]
        public void FirstByteTimeout_DefaultIsAtMostTenSeconds()
        {
            Assert.LessOrEqual(PlatformConfig.DefaultReusedConnectionFirstByteTimeout, TimeSpan.FromSeconds(10));
            Assert.Greater(PlatformConfig.DefaultReusedConnectionFirstByteTimeout, TimeSpan.Zero);
        }
    }
}
