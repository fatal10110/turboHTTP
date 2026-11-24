using System;
using System.Net.Sockets;
using System.Threading;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Transport.Connection
{
    /// <summary>
    /// Watches a pending socket connect (<c>Socket.ConnectAsync</c>) on Windows for a failure
    /// the runtime's I/O selector does not report (#1259), and cancels <see cref="Token"/> with the
    /// socket error in <see cref="Error"/> when it finds one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows reports a failed non-blocking connect (refused, unreachable) only in the exception set
    /// of <c>select</c>, never as writable. The I/O selector of Unity's runtimes waits on every
    /// pending socket operation of the process with one <c>select</c> call, and IL2CPP's Windows
    /// socket layer (libil2cpp <c>SocketImpl::Poll</c>) builds its sets with the Winsock default
    /// <c>FD_SETSIZE</c> of 64: <c>FD_SET</c> drops every socket past the 64th without error. Once 64
    /// or more sockets are registered with the selector ahead of a connecting socket, a refused
    /// connect is never reported and the connect task never ends: the caller saw its deadline
    /// (<see cref="TimeoutException"/>, or a WebSocket handshake <c>Timeout</c>) instead of the
    /// refusal. A successful connect is still reported through the write set, which holds only the
    /// few sockets waiting to write.
    /// </para>
    /// <para>
    /// The watch asks the socket itself, every <see cref="PollInterval"/>, with a zero-timeout
    /// <see cref="Socket.Poll(int, SelectMode)"/> for <see cref="SelectMode.SelectError"/> (a
    /// one-socket <c>select</c>, which the cap cannot affect) and reads <c>SO_ERROR</c> when it
    /// reports an error. It runs on Windows only: elsewhere the exception set means out-of-band data,
    /// not a connect failure, and the runtimes poll with <c>poll</c>, which reports the failure.
    /// </para>
    /// <para>
    /// Threading: the timer callback is the only writer of <see cref="Error"/> (Volatile) and the only
    /// caller of <c>Cancel</c>; it re-arms itself after each check, so checks never overlap.
    /// <see cref="Dispose"/> may race a check: a check after disposal returns at once, and
    /// <see cref="ObjectDisposedException"/> from the socket, the timer or the token source is ignored.
    /// </para>
    /// </remarks>
    internal sealed class ConnectFailureWatch : IDisposable
    {
        /// <summary>How often the pending connect is checked. A refused loopback connect takes about 2 s on Windows.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private static readonly TimerCallback s_check = static state => ((ConnectFailureWatch)state).Check();

        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts;
        private readonly Timer _timer;

        // Volatile: written once by the timer callback, read by the awaiting connect.
        private int _error;

        // Interlocked: 1 once disposed.
        private int _disposed;

        /// <summary>
        /// Starts watching <paramref name="socket"/>, whose connect is pending. <see cref="Token"/> is
        /// cancelled by <paramref name="cancellationToken"/> or by a connect failure.
        /// </summary>
        internal ConnectFailureWatch(Socket socket, CancellationToken cancellationToken)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Armed only once the field is set: the callback re-arms through it.
            _timer = new Timer(s_check, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(PollInterval, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// True where a pending connect needs the watch: Windows, where the runtime's selector can miss
        /// a connect failure (see remarks).
        /// </summary>
        internal static bool IsNeeded => SaeaPlatformDetection.CurrentOs == SocketOsFamily.Windows;

        /// <summary>Cancelled by the caller's token or once a connect failure was found.</summary>
        internal CancellationToken Token => _cts.Token;

        /// <summary>The socket error of a failed connect (<c>SO_ERROR</c>), or 0 while none was found.</summary>
        internal int Error => Volatile.Read(ref _error);

        private void Check()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            int error = 0;
            try
            {
                if (_socket.Poll(0, SelectMode.SelectError))
                    error = (int)_socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // The socket cannot be asked; the connect's own completion or deadline decides.
                return;
            }

            if (error != 0)
            {
                Volatile.Write(ref _error, error);
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }

            try { _timer.Change(PollInterval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        /// <summary>Stops the watch. The socket is not touched.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _timer.Dispose();
            _cts.Dispose();
        }
    }
}
