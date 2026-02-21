using System.Runtime.CompilerServices;

namespace TurboHTTP.WebSocket.Transport
{
    internal static class WebSocketTransportModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            RawSocketWebSocketTransport.EnsureRegistered();
        }
    }
}
