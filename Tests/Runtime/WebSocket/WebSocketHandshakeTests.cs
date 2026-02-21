using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketHandshakeTests
    {
        [Test]
        public void BuildRequest_StripsFragment_AndOmitsDefaultPort()
        {
            var request = WebSocketHandshake.BuildRequest(
                new Uri("ws://example.com/chat/room?q=1#fragment"));

            Assert.AreEqual("/chat/room?q=1", request.RequestTarget);
            Assert.AreEqual("example.com", request.HostHeader);
        }

        [Test]
        public void BuildRequest_IncludesNonDefaultPortInHostHeader()
        {
            var request = WebSocketHandshake.BuildRequest(
                new Uri("ws://example.com:8080/ws"));

            Assert.AreEqual("example.com:8080", request.HostHeader);
        }

        [Test]
        public void ValidateAsync_Succeeds_WithTokenizedConnectionHeader()
        {
            AssertAsync.Run(async () =>
            {
                var request = WebSocketHandshake.BuildRequest(
                    new Uri("ws://localhost/socket"),
                    subProtocols: new[] { "chat", "json" });

                string accept = WebSocketConstants.ComputeAcceptKey(request.ClientKey);
                var responseBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: WebSocket\r\n" +
                    "Connection: keep-alive, Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n" +
                    "Sec-WebSocket-Protocol: json\r\n" +
                    "\r\n");

                using var stream = new MemoryStream(responseBytes);
                var result = await WebSocketHandshakeValidator.ValidateAsync(
                    stream,
                    request,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(101, result.StatusCode);
                Assert.AreEqual("json", result.NegotiatedSubProtocol);
            });
        }

        [Test]
        public void ValidateAsync_Fails_WhenAcceptMismatch()
        {
            AssertAsync.Run(async () =>
            {
                var request = WebSocketHandshake.BuildRequest(new Uri("ws://localhost/socket"));

                using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: invalid\r\n\r\n"));

                var result = await WebSocketHandshakeValidator.ValidateAsync(
                    stream,
                    request,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(result.Success);
                StringAssert.Contains("Sec-WebSocket-Accept mismatch", result.ErrorMessage);
            });
        }

        [Test]
        public void ValidateAsync_Fails_WhenServerSelectsUnsupportedSubprotocol()
        {
            AssertAsync.Run(async () =>
            {
                var request = WebSocketHandshake.BuildRequest(
                    new Uri("ws://localhost/socket"),
                    subProtocols: new[] { "chat" });

                string accept = WebSocketConstants.ComputeAcceptKey(request.ClientKey);
                using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n" +
                    "Sec-WebSocket-Protocol: xml\r\n\r\n"));

                var result = await WebSocketHandshakeValidator.ValidateAsync(
                    stream,
                    request,
                    CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(result.Success);
                StringAssert.Contains("unsupported sub-protocol", result.ErrorMessage);
            });
        }

        [Test]
        public void BuildRequest_RejectsCRLFInjectionInCustomHeaders()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                WebSocketHandshake.BuildRequest(
                    new Uri("ws://localhost/socket"),
                    customHeaders: new[] { new KeyValuePair<string, string>("X-Test", "ok\r\nX-Bad: 1") });
            });
        }
    }
}
