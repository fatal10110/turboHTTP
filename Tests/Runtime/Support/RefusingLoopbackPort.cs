using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// A loopback port that refuses connections for as long as this object lives (#1259). On Windows a
    /// socket is bound to <c>127.0.0.1:0</c> and never listens, so a connect to <see cref="Port"/> is
    /// refused while the port stays bound to this socket; elsewhere <see cref="Port"/> is 1, a port with
    /// no socket (see remarks). Use it instead of starting and stopping a <see cref="TcpListener"/> to
    /// find a "closed" port: a released ephemeral port can be taken by another test's listener before
    /// the connect, which then is accepted and hangs instead of being refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows (Editor Mono and IL2CPP players, where #1259 failed) no other socket can bind the held
    /// port. Only Windows needs that hold: a bound non-listening socket elsewhere does not keep the port
    /// for itself (.NET on Unix sets <c>SO_REUSEADDR</c> on every socket) or does not refuse at all:
    /// Darwin (macOS, iOS) drops a SYN to a bound socket that does not listen, so the connect timed out
    /// (about 8 s) instead of being refused (#1286). Off Windows no socket is held and
    /// <see cref="Port"/> is 1: TurboHTTP tests only bind port 0, and every other OS refuses a connect
    /// to a port with no socket at once.
    /// </para>
    /// <para>
    /// A refused loopback connect is not instant on Windows: the client retries the SYN after the
    /// reset for about 2 s before it reports the refusal (#226). Bounds that wrap the connect must
    /// allow for that.
    /// </para>
    /// </remarks>
    internal sealed class RefusingLoopbackPort : IDisposable
    {
        private const int UnboundPort = 1;

        private readonly Socket _socket;

        public RefusingLoopbackPort()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Port = UnboundPort;
                return;
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // No address reuse requested (the default on Windows and Mono). It does not clear
                // SO_REUSEADDR on .NET for Unix, which sets it on every socket (see remarks).
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

        /// <summary>
        /// The refusing port: the held port on Windows, 1 elsewhere. Connects to <c>127.0.0.1:Port</c> are
        /// refused until <see cref="Dispose"/>.
        /// </summary>
        public int Port { get; }

        /// <summary>Releases the held port (Windows); nothing to release elsewhere.</summary>
        public void Dispose() => _socket?.Dispose();
    }
}
