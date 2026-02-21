using System;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketReconnectPolicyTests
    {
        [Test]
        public void ComputeDelay_AttemptOne_ReturnsInitialDelay_WhenJitterDisabled()
        {
            var policy = new WebSocketReconnectPolicy(
                maxRetries: 3,
                initialDelay: TimeSpan.FromSeconds(1),
                maxDelay: TimeSpan.FromSeconds(30),
                backoffMultiplier: 2.0,
                jitterFactor: 0.0);

            var delay = policy.ComputeDelay(1);

            Assert.AreEqual(TimeSpan.FromSeconds(1), delay);
        }

        [Test]
        public void ComputeDelay_CapsAtMaxDelay()
        {
            var policy = new WebSocketReconnectPolicy(
                maxRetries: 10,
                initialDelay: TimeSpan.FromSeconds(1),
                maxDelay: TimeSpan.FromSeconds(5),
                backoffMultiplier: 3.0,
                jitterFactor: 0.0);

            var delay = policy.ComputeDelay(6);

            Assert.AreEqual(TimeSpan.FromSeconds(5), delay);
        }

        [Test]
        public void ShouldReconnect_RespectsMaxRetries()
        {
            var policy = new WebSocketReconnectPolicy(
                maxRetries: 2,
                initialDelay: TimeSpan.FromMilliseconds(100),
                maxDelay: TimeSpan.FromSeconds(1),
                backoffMultiplier: 2.0,
                jitterFactor: 0.0);

            Assert.IsTrue(policy.ShouldReconnect(1, WebSocketCloseCode.AbnormalClosure));
            Assert.IsTrue(policy.ShouldReconnect(2, WebSocketCloseCode.AbnormalClosure));
            Assert.IsFalse(policy.ShouldReconnect(3, WebSocketCloseCode.AbnormalClosure));
        }

        [Test]
        public void ShouldReconnect_RespectsCloseCodePredicate()
        {
            var policy = new WebSocketReconnectPolicy(
                maxRetries: 5,
                reconnectOnCloseCode: code => code == WebSocketCloseCode.AbnormalClosure);

            Assert.IsTrue(policy.ShouldReconnect(1, WebSocketCloseCode.AbnormalClosure));
            Assert.IsFalse(policy.ShouldReconnect(1, WebSocketCloseCode.ProtocolError));
        }

        [Test]
        public void NonePolicy_DisablesReconnect()
        {
            Assert.IsFalse(WebSocketReconnectPolicy.None.Enabled);
            Assert.IsFalse(WebSocketReconnectPolicy.None.ShouldReconnect(1, WebSocketCloseCode.AbnormalClosure));
        }
    }
}
