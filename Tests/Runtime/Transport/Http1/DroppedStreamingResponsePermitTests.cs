using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #583: an HTTP/1.1 streaming response dropped mid-body without Dispose must not keep its
    /// connection lease, and with it the per-host permit, forever. After MaxConnectionsPerHost
    /// dropped responses to one host, a further request to that host still gets a permit.
    /// </summary>
    [TestFixture]
    public class DroppedStreamingResponsePermitTests
    {
        private const int MaxConnectionsPerHost = 4;

        [SetUp]
        public void SetUp()
        {
            // Keeps the deliberate leak below out of the run log, where it would look like a real one.
            TurboDiagnostics.SetSink(_ => { });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.ResetSink();
        }

        [Test]
        public void DroppedMidBodyResponses_ReleaseTheirPerHostPermits()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HoldingServer(MaxConnectionsPerHost);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: MaxConnectionsPerHost);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    AllowCleartext = true,
                    Transport = new RawSocketTransport(pool),
                    DisposeTransport = true
                });
                var url = $"http://127.0.0.1:{server.Port}/";

                // Every permit is now held by a response whose body is still open on the server side.
                var dropped = await DropStreamingResponsesAsync(client, url, MaxConnectionsPerHost);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                int finalized = 0;
                foreach (var reference in dropped)
                {
                    if (!reference.IsAlive)
                        finalized++;
                }

                Assert.That(finalized, Is.GreaterThan(0), "no dropped response was finalized; nothing to test");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                UHttpStreamingResponse next;
                try
                {
                    next = await client.SendStreamingAsync(new UHttpRequest(HttpMethod.GET, new Uri(url)), cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is UHttpException)
                {
                    Assert.Fail($"No per-host permit was released by {finalized} finalized response(s) (#583): {ex.GetType().Name}: {ex.Message}");
                    return;
                }
                finally
                {
                    // Instances a conservative stack scan kept alive are disposed here.
                    foreach (var reference in dropped)
                        (reference.Target as UHttpStreamingResponse)?.Dispose();
                }

                using (next)
                    Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference[]> DropStreamingResponsesAsync(UHttpClient client, string url, int count)
        {
            var references = new WeakReference[count];
            for (int i = 0; i < count; i++)
            {
                var response = await client.SendStreamingAsync(new UHttpRequest(HttpMethod.GET, new Uri(url)));
                references[i] = new WeakReference(response);
                // Debug builds hoist locals into the state machine; clear it so only the weak reference remains.
                response = null;
            }

            return references;
        }

        /// <summary>
        /// The first <c>holdCount</c> connections get a response head with a long Content-Length and a few
        /// body bytes, then stay open; later connections get a complete empty response.
        /// </summary>
        private sealed class HoldingServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _holdCount;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;

            public HoldingServer(int holdCount)
            {
                _holdCount = holdCount;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                            break;
                        continue;
                    }

                    lock (_lock)
                        _clients.Add(client);

                    int index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => ServeAsync(client, index));
                }
            }

            private async Task ServeAsync(TcpClient client, int index)
            {
                try
                {
                    var stream = client.GetStream();
                    while (await ReadRequestHeadersAsync(stream))
                    {
                        string response = index <= _holdCount
                            ? "HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\npartial"
                            : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
                        var bytes = Encoding.ASCII.GetBytes(response);
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                        await stream.FlushAsync();
                        if (index <= _holdCount)
                            return; // the body never completes; Dispose closes the socket
                    }
                }
                catch (Exception)
                {
                    // The client closed the connection, or the server is being disposed.
                }
            }

            private static async Task<bool> ReadRequestHeadersAsync(NetworkStream stream)
            {
                var buffer = new byte[1];
                int matched = 0; // matches \r\n\r\n
                while (matched < 4)
                {
                    int read = await stream.ReadAsync(buffer, 0, 1);
                    if (read == 0)
                        return false;
                    byte b = buffer[0];
                    matched = (matched % 2 == 0 && b == '\r') || (matched % 2 == 1 && b == '\n')
                        ? matched + 1
                        : (b == '\r' ? 1 : 0);
                }

                return true;
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }

                    _clients.Clear();
                }
            }
        }
    }
}
