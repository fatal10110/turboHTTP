using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #217: the Autobahn interop run found Sec-WebSocket-Accept computed with a wrong GUID, so every
    /// handshake with a real server failed. The in-house fixtures shared the same helper and agreed with it;
    /// these tests pin the values RFC 6455 itself publishes instead.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketAcceptKeyTests
    {
        [Test]
        public void Guid_IsTheRfc6455MagicString()
        {
            // RFC 6455 section 1.3 and 4.2.2 item 5.4.
            Assert.AreEqual("258EAFA5-E914-47DA-95CA-C5AB0DC85B11", WebSocketConstants.WebSocketGuid);
        }

        [Test]
        public void ComputeAcceptKey_Rfc6455SampleNonce_MatchesPublishedAccept()
        {
            // RFC 6455 section 1.3: key "dGhlIHNhbXBsZSBub25jZQ==" -> accept "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=".
            Assert.AreEqual(
                "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
                WebSocketConstants.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));
        }
    }
}
