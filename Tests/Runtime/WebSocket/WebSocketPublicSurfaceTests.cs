using System;
using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// Guards the public surface of the TurboHTTP.WebSocket assembly before the 1.0 API freeze (#1145):
    /// frame, assembler, handshake, extension-negotiation and connection plumbing stay internal, and every
    /// public exception follows the package error model (UHttpException).
    /// </summary>
    [TestFixture]
    public sealed class WebSocketPublicSurfaceTests
    {
        // Adding a public type to TurboHTTP.WebSocket is a 1.0 API decision: document it and add it here.
        private static readonly string[] ExpectedPublicTypes =
        {
            "TurboHTTP.WebSocket.ConnectionQuality",
            "TurboHTTP.WebSocket.IWebSocketClient",
            "TurboHTTP.WebSocket.IWebSocketExtension",
            "TurboHTTP.WebSocket.IWebSocketMessageSerializer`1",
            "TurboHTTP.WebSocket.IWebSocketTransport",
            "TurboHTTP.WebSocket.JsonWebSocketSerializer`1",
            "TurboHTTP.WebSocket.PerMessageDeflateExtension",
            "TurboHTTP.WebSocket.PerMessageDeflateOptions",
            "TurboHTTP.WebSocket.ProxyCredentials",
            "TurboHTTP.WebSocket.RawStringSerializer",
            "TurboHTTP.WebSocket.ResilientWebSocketClient",
            "TurboHTTP.WebSocket.WebSocketClient",
            "TurboHTTP.WebSocket.WebSocketClientExtensions",
            "TurboHTTP.WebSocket.WebSocketCloseCode",
            "TurboHTTP.WebSocket.WebSocketConnectionOptions",
            "TurboHTTP.WebSocket.WebSocketError",
            "TurboHTTP.WebSocket.WebSocketException",
            "TurboHTTP.WebSocket.WebSocketExtensionOffer",
            "TurboHTTP.WebSocket.WebSocketExtensionParameters",
            "TurboHTTP.WebSocket.WebSocketHealthSnapshot",
            "TurboHTTP.WebSocket.WebSocketMessage",
            "TurboHTTP.WebSocket.WebSocketMessageType",
            "TurboHTTP.WebSocket.WebSocketMetrics",
            "TurboHTTP.WebSocket.WebSocketOpcode",
            "TurboHTTP.WebSocket.WebSocketProxySettings",
            "TurboHTTP.WebSocket.WebSocketReconnectPolicy",
            "TurboHTTP.WebSocket.WebSocketState",
            "TurboHTTP.WebSocket.WebSocketTransportFactory",
        };

        [Test]
        public void ExportedTypes_AreExactlyTheClientOptionsMessageExtensionSerializerAndTransportContracts()
        {
            var actual = new List<string>();
            foreach (var type in typeof(WebSocketClient).Assembly.GetExportedTypes())
                actual.Add(type.FullName);
            actual.Sort(StringComparer.Ordinal);

            var expected = new List<string>(ExpectedPublicTypes);
            expected.Sort(StringComparer.Ordinal);

            var unexpected = new List<string>();
            foreach (var name in actual)
            {
                if (!expected.Contains(name))
                    unexpected.Add(name);
            }

            var missing = new List<string>();
            foreach (var name in expected)
            {
                if (!actual.Contains(name))
                    missing.Add(name);
            }

            Assert.IsEmpty(unexpected, "Public types not in the 1.0 WebSocket surface: " + string.Join(", ", unexpected));
            Assert.IsEmpty(missing, "Expected public WebSocket types missing: " + string.Join(", ", missing));
        }

        [Test]
        public void ExportedExceptions_DeriveFromUHttpException()
        {
            foreach (var type in typeof(WebSocketClient).Assembly.GetExportedTypes())
            {
                if (!typeof(Exception).IsAssignableFrom(type))
                    continue;

                Assert.IsTrue(
                    typeof(UHttpException).IsAssignableFrom(type),
                    type.FullName + " is public but does not derive from UHttpException.");
            }
        }

        [Test]
        public void WebSocketClient_IsSealed()
        {
            Assert.IsTrue(typeof(WebSocketClient).IsSealed);
        }
    }
}
