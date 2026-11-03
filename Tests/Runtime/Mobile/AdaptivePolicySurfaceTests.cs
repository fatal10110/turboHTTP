using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// #153: AdaptivePolicy exposes only options the timeout-only AdaptiveInterceptor reads.
    /// </summary>
    [TestFixture]
    public class AdaptivePolicySurfaceTests
    {
        [Test]
        public void PublicOptions_AreOnlyTheTimeoutAdaptationOptions()
        {
            var names = typeof(AdaptivePolicy)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(names, Is.EqualTo(new[] { "Enable", "MaxTimeout", "MinTimeout" }));
        }

        [Test]
        public void Clone_CopiesEveryOption_AndIsIndependent()
        {
            var policy = new AdaptivePolicy
            {
                Enable = true,
                MinTimeout = TimeSpan.FromSeconds(3),
                MaxTimeout = TimeSpan.FromSeconds(9)
            };

            var clone = policy.Clone();
            policy.Enable = false;
            policy.MinTimeout = TimeSpan.FromSeconds(1);
            policy.MaxTimeout = TimeSpan.FromSeconds(2);

            Assert.That(clone.Enable, Is.True);
            Assert.That(clone.MinTimeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(clone.MaxTimeout, Is.EqualTo(TimeSpan.FromSeconds(9)));
        }
    }
}
