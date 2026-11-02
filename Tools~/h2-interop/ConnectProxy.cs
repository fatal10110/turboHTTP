using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tools.H2Interop
{
    /// <summary>
    /// A minimal HTTP CONNECT proxy (RFC 9110 section 9.3.6) on an ephemeral loopback port, for the
    /// BouncyCastle-through-a-tunnel rows (#255). It answers <c>CONNECT 127.0.0.1:port HTTP/1.1</c> with
    /// <c>200</c>, then relays raw bytes both ways, so the TLS handshake (ALPN) and the HTTP/2 connection run
    /// end to end between the client and the origin. Only loopback targets are tunneled; anything else gets
    /// <c>403</c>, a malformed request <c>400</c>. The request header block is bounded by
    /// <see cref="MaxHeaderBytes"/>.
    /// </summary>
    internal sealed class ConnectProxy : IAsyncDisposable
    {
        private const int MaxHeaderBytes = 8192;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _tunnels = new List<Task>();
        private readonly List<string> _targets = new List<string>();
        private readonly Task _acceptLoop;
        private int _acceptCount;

        private ConnectProxy(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>TCP connections the proxy accepted (one per tunnel the client opened).</summary>
        public int AcceptCount => Volatile.Read(ref _acceptCount);

        /// <summary>The <c>host:port</c> authority of every CONNECT request, in arrival order.</summary>
        public string[] ConnectTargets
        {
            get
            {
                lock (_targets)
                    return _targets.ToArray();
            }
        }

        public static ConnectProxy Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ConnectProxy(listener);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _acceptCount);
                lock (_tunnels)
                    _tunnels.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(Socket client)
        {
            Socket upstream = null;
            try
            {
                using var clientStream = new NetworkStream(client, ownsSocket: true);
                var buffer = new byte[MaxHeaderBytes];
                int filled = 0, headerEnd = -1;
                while (headerEnd < 0)
                {
                    if (filled == buffer.Length)
                    {
                        await ReplyAsync(clientStream, "431 Request Header Fields Too Large").ConfigureAwait(false);
                        return;
                    }

                    int n = await clientStream.ReadAsync(buffer, filled, buffer.Length - filled, _cts.Token).ConfigureAwait(false);
                    if (n == 0)
                        return;
                    filled += n;
                    headerEnd = IndexOfHeaderEnd(buffer, filled);
                }

                string requestLine = Encoding.ASCII.GetString(buffer, 0, Array.IndexOf(buffer, (byte)'\r'));
                string[] parts = requestLine.Split(' ');
                int colon = parts.Length == 3 ? parts[1].LastIndexOf(':') : -1;
                if (parts.Length != 3 || parts[0] != "CONNECT" || parts[2] != "HTTP/1.1" || colon <= 0 ||
                    !int.TryParse(parts[1].Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                    port <= 0 || port > 65535)
                {
                    await ReplyAsync(clientStream, "400 Bad Request").ConfigureAwait(false);
                    return;
                }

                string host = parts[1].Substring(0, colon);
                lock (_targets)
                    _targets.Add(parts[1]);
                if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
                {
                    await ReplyAsync(clientStream, "403 Forbidden").ConfigureAwait(false);
                    return;
                }

                upstream = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await upstream.ConnectAsync(new IPEndPoint(address, port), _cts.Token).ConfigureAwait(false);
                using var upstreamStream = new NetworkStream(upstream, ownsSocket: true);
                upstream = null;

                await ReplyAsync(clientStream, "200 Connection Established").ConfigureAwait(false);

                // Bytes the client sent after the header block belong to the tunnel.
                int leftover = filled - headerEnd;
                if (leftover > 0)
                    await upstreamStream.WriteAsync(buffer, headerEnd, leftover, _cts.Token).ConfigureAwait(false);

                var up = clientStream.CopyToAsync(upstreamStream, _cts.Token);
                var down = upstreamStream.CopyToAsync(clientStream, _cts.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A tunnel ends with either side closing, resetting or the proxy stopping.
            }
            finally
            {
                upstream?.Dispose();
            }
        }

        private Task ReplyAsync(NetworkStream stream, string status)
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n\r\n");
            return stream.WriteAsync(bytes, 0, bytes.Length, _cts.Token);
        }

        /// <summary>Offset just past the first CRLFCRLF in <paramref name="buffer"/>[0..count), or -1.</summary>
        private static int IndexOfHeaderEnd(byte[] buffer, int count)
        {
            for (int i = 3; i < count; i++)
            {
                if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
                    return i + 1;
            }

            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _acceptLoop.ConfigureAwait(false);
            Task[] tunnels;
            lock (_tunnels)
                tunnels = _tunnels.ToArray();
            await Task.WhenAll(tunnels).ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
