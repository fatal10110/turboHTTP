using System;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Factory registration point for default WebSocket transports.
    /// </summary>
    public static class WebSocketTransportFactory
    {
        private static volatile Func<TlsBackend, IWebSocketTransport> _backendFactory;
        private static readonly object Gate = new object();

        /// <summary>Registers the factory <see cref="Create"/> uses; the last registration wins. <c>RawSocketWebSocketTransport</c> registers itself.</summary>
        public static void Register(Func<TlsBackend, IWebSocketTransport> backendFactory)
        {
            if (backendFactory == null)
                throw new ArgumentNullException(nameof(backendFactory));

            lock (Gate)
            {
                _backendFactory = backendFactory;
            }
        }

        /// <summary>Creates a transport through the registered factory. Throws <see cref="InvalidOperationException"/> when none is registered.</summary>
        public static IWebSocketTransport Create(TlsBackend tlsBackend = TlsBackend.Auto)
        {
            var factory = _backendFactory;
            if (factory == null)
            {
                throw new InvalidOperationException(
                    "No WebSocket transport factory configured. " +
                    "Ensure TurboHTTP.WebSocket.Transport and TurboHTTP.Complete are included in the " +
                    "build (the platform must not be WebGL), or call " +
                    "RawSocketWebSocketTransport.EnsureRegistered() at startup.");
            }

            return factory(tlsBackend);
        }

        internal static void Reset()
        {
            lock (Gate)
            {
                _backendFactory = null;
            }
        }
    }
}
