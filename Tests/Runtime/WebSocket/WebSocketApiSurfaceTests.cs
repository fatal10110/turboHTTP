using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketApiSurfaceTests
    {
        [Test]
        public void WebSocketException_IsRetryable_ForNetworkStyleErrors()
        {
            var ex = new WebSocketException(WebSocketError.ConnectionClosed, "closed");
            Assert.IsTrue(ex.IsRetryable());
        }

        [Test]
        public void WebSocketException_IsRetryable_FalseForProtocolViolations()
        {
            var ex = new WebSocketException(WebSocketError.ProtocolViolation, "protocol");
            Assert.IsFalse(ex.IsRetryable());
        }

        [Test]
        public void WebSocketException_IsRetryable_ForProxyConnectionFailure()
        {
            var ex = new WebSocketException(WebSocketError.ProxyConnectionFailed, "proxy");
            Assert.IsTrue(ex.IsRetryable());
        }

        [Test]
        public void WebSocketException_IsRetryable_FalseForSerializationFailure()
        {
            var ex = new WebSocketException(WebSocketError.SerializationFailed, "serialization");
            Assert.IsFalse(ex.IsRetryable());
        }
    }
}
