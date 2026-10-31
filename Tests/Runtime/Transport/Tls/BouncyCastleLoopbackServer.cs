using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// Loopback server for BouncyCastle client tests. Accepts any number of connections; each runs
    /// on its own dedicated (non-pool) thread, so tests that clamp the thread pool measure only the
    /// client. With a certificate the connection is a TLS 1.2 <see cref="SslStream"/> server
    /// (self-signed CN=localhost: accepted by the BouncyCastle client while chain validation, #33,
    /// is not implemented); without one the handler gets the raw socket stream (scripted bytes).
    /// </summary>
    internal sealed class BouncyCastleLoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly Action<Stream> _handler;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _clientsLock = new object();
        private int _accepted;
        private Exception _handlerError;
        private int _disposed;

        private BouncyCastleLoopbackServer(X509Certificate2 certificate, Action<Stream> handler)
        {
            _certificate = certificate;
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            StartThread(AcceptLoop, "bc-test-accept");
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int AcceptedConnections => Volatile.Read(ref _accepted);

        /// <summary>First exception a handler threw (null if none).</summary>
        public Exception HandlerError => Volatile.Read(ref _handlerError);

        /// <summary>TLS server; null when the runtime cannot create a self-signed certificate.</summary>
        public static BouncyCastleLoopbackServer TryStartTls(Action<Stream> handler)
        {
            var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
            return certificate == null ? null : new BouncyCastleLoopbackServer(certificate, handler);
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

                StartThread(() => Serve(client), "bc-test-conn");
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

        private static void StartThread(ThreadStart body, string name)
        {
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }
    }
}
