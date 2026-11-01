using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// Loopback server for BouncyCastle client tests. Accepts any number of connections; with a
    /// synchronous handler each runs on its own dedicated thread. With a certificate the connection
    /// is a TLS 1.2 <see cref="SslStream"/> server (the committed CN=localhost test certificate);
    /// without one the handler gets the raw socket stream (scripted bytes).
    /// <para>
    /// A dedicated thread does not keep the server off the thread pool on Unity's Mono: its
    /// synchronous SslStream API runs every inner read and write as <c>Task.Run(() => inner.Read(...))</c>
    /// and waits for it (<c>Mono.Net.Security.MobileAuthenticatedStream.InnerRead</c>), so each
    /// blocked server read holds a pool worker (#206). Tests that clamp the pool use
    /// <see cref="TryStartTlsAsync"/>, whose handshake and handler never block a pool thread.
    /// </para>
    /// </summary>
    internal sealed class BouncyCastleLoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly Action<Stream> _handler;
        private readonly Func<Stream, Task> _asyncHandler;
        private readonly List<Task> _serveTasks = new List<Task>();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _clientsLock = new object();
        private int _accepted;
        private Exception _handlerError;
        private int _disposed;

        private BouncyCastleLoopbackServer(
            X509Certificate2 certificate, Action<Stream> handler, Func<Stream, Task> asyncHandler = null)
        {
            _certificate = certificate;
            _handler = handler;
            _asyncHandler = asyncHandler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            StartThread(AcceptLoop, "bc-test-accept");
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int AcceptedConnections => Volatile.Read(ref _accepted);

        /// <summary>First exception a handler threw (null if none).</summary>
        public Exception HandlerError => Volatile.Read(ref _handlerError);

        /// <summary>TLS server; null when the runtime cannot load the committed test certificate.</summary>
        public static BouncyCastleLoopbackServer TryStartTls(Action<Stream> handler)
        {
            var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
            return certificate == null ? null : new BouncyCastleLoopbackServer(certificate, handler);
        }

        /// <summary>
        /// TLS server with an asynchronous handshake and handler: no thread, pool or dedicated, is
        /// blocked while it waits for the client. Null when the runtime cannot load the committed
        /// test certificate.
        /// </summary>
        public static BouncyCastleLoopbackServer TryStartTlsAsync(Func<Stream, Task> handler)
        {
            var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
            return certificate == null ? null : new BouncyCastleLoopbackServer(certificate, null, handler);
        }

        /// <summary>Plain TCP server for scripted TLS records.</summary>
        public static BouncyCastleLoopbackServer StartRaw(Action<Stream> handler)
        {
            return new BouncyCastleLoopbackServer(null, handler);
        }

        /// <summary>Fails the test unless the BouncyCastle assembly is part of the build (#140).</summary>
        public static ITlsProvider GetProviderOrFail()
        {
            return TestRequirements.RequireBouncyCastleProvider();
        }

        /// <summary>
        /// Client side: a <see cref="NetworkStream"/> over a socket connected to the loopback port.
        /// The stream owns the socket.
        /// </summary>
        public static async Task<Stream> ConnectAsync(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        /// <summary>
        /// Sends the server's close_notify (RFC 5246 section 7.2.1, RFC 8446 section 6.1) and
        /// waits until it is written. On .NET <see cref="SslStream.ShutdownAsync"/> always sends
        /// it. Unity's Mono SslStream (UnityTls) sends it only when the internal
        /// <c>MonoTlsSettings.SendCloseNotify</c> flag is set, which defaults to false
        /// (<c>Mono.Unity.UnityTlsContext.Shutdown</c>), so a plain ShutdownAsync puts nothing on
        /// the wire there (#206). The flag is set on this stream's own settings copy.
        /// </summary>
        public static void SendCloseNotify(SslStream ssl)
        {
            if (Type.GetType("Mono.Runtime") != null)
            {
                var settingsField = typeof(SslStream).GetField(
                    "settings", BindingFlags.Instance | BindingFlags.NonPublic);
                object settings = settingsField?.GetValue(ssl);
                var sendCloseNotify = settings?.GetType().GetProperty(
                    "SendCloseNotify", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sendCloseNotify == null)
                {
                    throw new InvalidOperationException(
                        "Mono SslStream settings.SendCloseNotify not found; the server cannot send close_notify.");
                }

                sendCloseNotify.SetValue(settings, true);
            }

            ssl.ShutdownAsync().GetAwaiter().GetResult();
        }

        public static async Task ReadExactAsync(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        /// <summary>Completes when the peer closes (or the socket fails).</summary>
        public static async Task DrainUntilClosedAsync(Stream stream)
        {
            var buffer = new byte[1024];
            try
            {
                while (await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
                {
                    // Discard: only the close matters.
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Blocks until the peer closes (or the socket fails or times out).</summary>
        public static void DrainUntilClosed(Stream stream)
        {
            var buffer = new byte[1024];
            try
            {
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                    // Discard: only the close matters.
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _listener.Stop(); } catch { }
            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();
            }
            _certificate?.Dispose();
        }

        private void AcceptLoop()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    return; // listener stopped
                }

                Interlocked.Increment(ref _accepted);
                lock (_clientsLock)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        client.Close();
                        return;
                    }
                    _clients.Add(client);
                }

                if (_asyncHandler != null)
                {
                    // Owned by the server: Dispose closes the client, which ends the task; every
                    // exception is caught inside ServeAsync.
                    var serve = ServeAsync(client);
                    lock (_clientsLock)
                    {
                        _serveTasks.Add(serve);
                    }
                }
                else
                {
                    StartThread(() => Serve(client), "bc-test-conn");
                }
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                // Bounds a handler whose client never answers (pre-fix deadlocks).
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                Stream stream = client.GetStream();
                if (_certificate != null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    ssl.AuthenticateAsServer(_certificate, false, SslProtocols.Tls12, false);
                    stream = ssl;
                }

                using (stream)
                {
                    _handler(stream);
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Interlocked.CompareExchange(ref _handlerError, ex, null);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                Stream stream = client.GetStream();
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = ssl;
                using (stream)
                {
                    await ssl.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12, false)
                        .ConfigureAwait(false);
                    await _asyncHandler(stream).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Interlocked.CompareExchange(ref _handlerError, ex, null);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void StartThread(ThreadStart body, string name)
        {
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }
    }
}
