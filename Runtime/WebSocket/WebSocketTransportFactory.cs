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

        public static void Register(Func<TlsBackend, IWebSocketTransport> backendFactory)
        {
            if (backendFactory == null)
                throw new ArgumentNullException(nameof(backendFactory));

            lock (Gate)
            {
                _backendFactory = backendFactory;
            }
        }

        public static IWebSocketTransport Create(TlsBackend tlsBackend = TlsBackend.Auto)
        {
            var factory = _backendFactory;
            if (factory == null)
            {
                throw new InvalidOperationException(
                    "No WebSocket transport factory configured. " +
                    "Ensure TurboHTTP.WebSocket.Transport is included in your project. " +
                    "If module initializer ordering prevents registration, call " +
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
