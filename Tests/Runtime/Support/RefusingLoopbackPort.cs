using System;
using System.Net;
using System.Net.Sockets;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// A loopback port that refuses connections for as long as this object lives (#1259): a socket is
    /// bound to <c>127.0.0.1:0</c> and never listens, so a connect to <see cref="Port"/> gets a reset
    /// (connection refused), and the OS cannot hand the port to another socket that binds port 0.
    /// Use it instead of starting and stopping a <see cref="TcpListener"/> to find a "closed" port:
    /// a released ephemeral port can be taken by another test's listener before the connect, which
    /// then is accepted and hangs instead of being refused.
    /// </summary>
    /// <remarks>
    /// A refused loopback connect is not instant on Windows: the client retries the SYN after the
    /// reset for about 2 s before it reports the refusal (#226). Bounds that wrap the connect must
    /// allow for that.
    /// </remarks>
    internal sealed class RefusingLoopbackPort : IDisposable
    {
        private readonly Socket _socket;

        public RefusingLoopbackPort()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Explicit, whatever the runtime's default: a second socket cannot share the port
                // through SO_REUSEADDR on Linux and Android while this one holds it.
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_socket.LocalEndPoint).Port;
            }
            catch
            {
                _socket.Dispose();
                throw;
            }
        }

        /// <summary>The held port; connects to <c>127.0.0.1:Port</c> are refused until <see cref="Dispose"/>.</summary>
        public int Port { get; }

        /// <summary>Releases the port.</summary>
        public void Dispose() => _socket.Dispose();
    }
}
