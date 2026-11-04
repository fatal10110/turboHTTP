using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// #184: IsDefault() tracks explicit assignment, not value equality, so assigning a default
    /// value is still a configuration the client honours.
    /// </summary>
    [TestFixture]
    public class ConnectionPoolOptionsExplicitSetTests
    {
        [Test]
        public void Untouched_IsDefault_AndCloneStaysDefault()
        {
            var options = new ConnectionPoolOptions();
            Assert.IsTrue(options.IsDefault());
            Assert.IsTrue(options.Clone().IsDefault());
            Assert.IsTrue(new HappyEyeballsOptions().IsDefault());
        }

        [Test]
        public void AssigningDefaultValue_OfEachOption_IsNotDefault()
        {
            var probe = new ConnectionPoolOptions();
            var setters = new Action<ConnectionPoolOptions>[]
            {
                o => o.MaxConnectionsPerHost = probe.MaxConnectionsPerHost,
                o => o.ConnectionIdleTimeout = probe.ConnectionIdleTimeout,
                o => o.DnsTimeoutMs = probe.DnsTimeoutMs,
                o => o.ConnectTimeout = probe.ConnectTimeout,
                o => o.SocketIoMode = probe.SocketIoMode,
                o => o.HappyEyeballs = new HappyEyeballsOptions(),
                o => o.HappyEyeballs.FamilyStaggerDelay = probe.HappyEyeballs.FamilyStaggerDelay,
                o => o.HappyEyeballs.AttemptSpacingDelay = probe.HappyEyeballs.AttemptSpacingDelay,
                o => o.HappyEyeballs.MaxConcurrentAttempts = probe.HappyEyeballs.MaxConcurrentAttempts,
                o => o.HappyEyeballs.PreferIpv6 = probe.HappyEyeballs.PreferIpv6,
                o => o.HappyEyeballs.Enable = probe.HappyEyeballs.Enable,
            };

            for (int i = 0; i < setters.Length; i++)
            {
                var options = new ConnectionPoolOptions();
                setters[i](options);
                Assert.IsFalse(options.IsDefault(), $"setter #{i} assigned a default value but IsDefault() stayed true");
                Assert.IsFalse(options.Clone().IsDefault(), $"setter #{i}: Clone() lost the explicit-set state");
            }
        }

        [Test]
        public void HappyEyeballsClone_KeepsExplicitSetState_AndValues()
        {
            var happy = new HappyEyeballsOptions { MaxConcurrentAttempts = 3 };
            var clone = happy.Clone();
            Assert.IsFalse(clone.IsDefault());
            Assert.AreEqual(3, clone.MaxConcurrentAttempts);
            Assert.AreEqual(TimeSpan.FromMilliseconds(250), clone.FamilyStaggerDelay);
        }
    }
}
