using System;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class StreamingOptionsTests
    {
        [Test]
        public void Defaults_IncludeExpectContinueSettings()
        {
            var options = new StreamingOptions();

            Assert.AreEqual(1000, options.ExpectContinueTimeoutMs);
            Assert.IsNull(options.AutoExpectContinueThresholdBytes);
        }

        [Test]
        public void Clone_CopiesExpectContinueSettingsIndependently()
        {
            var options = new StreamingOptions
            {
                ExpectContinueTimeoutMs = 2500,
                AutoExpectContinueThresholdBytes = 64 * 1024
            };

            var clone = options.Clone();
            options.ExpectContinueTimeoutMs = 1500;
            options.AutoExpectContinueThresholdBytes = 1024;

            Assert.AreEqual(2500, clone.ExpectContinueTimeoutMs);
            Assert.AreEqual(64 * 1024, clone.AutoExpectContinueThresholdBytes);
            Assert.AreEqual(1500, options.ExpectContinueTimeoutMs);
            Assert.AreEqual(1024, options.AutoExpectContinueThresholdBytes);
        }

        [Test]
        public void ExpectContinueTimeoutMs_NonPositive_Throws()
        {
            var options = new StreamingOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.ExpectContinueTimeoutMs = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ExpectContinueTimeoutMs = -1);
        }

        [Test]
        public void AutoExpectContinueThresholdBytes_Negative_Throws()
        {
            var options = new StreamingOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.AutoExpectContinueThresholdBytes = -1);
        }
    }
}
