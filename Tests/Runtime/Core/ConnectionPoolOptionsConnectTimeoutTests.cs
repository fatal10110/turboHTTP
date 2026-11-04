using System;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    /// <summary>ConnectionPoolOptions.ConnectTimeout plumbing (#163).</summary>
    [TestFixture]
    public class ConnectionPoolOptionsConnectTimeoutTests
    {
        [Test]
        public void Default_IsTenSeconds_AndDefaultOptionsStayDefault()
        {
            var options = new ConnectionPoolOptions();
            Assert.AreEqual(TimeSpan.FromSeconds(10), options.ConnectTimeout);
            Assert.IsTrue(options.IsDefault());
        }

        [Test]
        public void Changed_IsNotDefault_AndCloneCopiesIt()
        {
            var options = new ConnectionPoolOptions { ConnectTimeout = TimeSpan.FromSeconds(3) };
            Assert.IsFalse(options.IsDefault());
            Assert.AreEqual(TimeSpan.FromSeconds(3), options.Clone().ConnectTimeout);
        }

        [Test]
        public void Infinite_IsAccepted()
        {
            var options = new ConnectionPoolOptions { ConnectTimeout = Timeout.InfiniteTimeSpan };
            Assert.AreEqual(Timeout.InfiniteTimeSpan, options.ConnectTimeout);
        }

        [Test]
        public void NonPositiveOrTooLarge_Throws()
        {
            var options = new ConnectionPoolOptions();
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ConnectTimeout = TimeSpan.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ConnectTimeout = TimeSpan.FromSeconds(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ConnectTimeout = TimeSpan.FromDays(30));
        }
    }
}
