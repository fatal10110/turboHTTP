using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #162: a peer that accepts and never writes must not hold a request past its timeout under
    /// <see cref="SocketIoMode.NetworkStream"/> (Mono/IL2CPP NetworkStream ignores the token once a
    /// read is pending). The connection is disposed, not pooled, and its permit is released.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportCancellationTests
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(500);

        // Guard only: the request must finish long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void HeaderPhase_SilentServer_TimesOutAndReleasesConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentServer();
                using var pool = new TcpConnectionPool(
                    maxConnectionsPerHost: 1,
                    socketIoMode: SocketIoMode.NetworkStream);
                using var transport = new RawSocketTransport(pool, TlsBackend.SslStream);
                var request = new UHttpRequest(
                        HttpMethod.GET,
                        new Uri("http://127.0.0.1:" + server.Port + "/silent"))
                    .WithTimeout(RequestTimeout);

                await AssertTimesOutAsync(transport, request);
                await AssertCompletesAsync(server.ClosedByClient(0), "connection was not disposed on timeout");

                // maxConnectionsPerHost = 1: a second connection proves the permit was released.
                await AssertTimesOutAsync(transport, request);
                await AssertCompletesAsync(server.Accepted(1), "per-host permit was not released");
            });
        }

        [Test]
        public void ConnectPhase_SilentProxy_TimesOutAndReleasesConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new SilentServer();
                using var pool = new TcpConnectionPool(
                    maxConnectionsPerHost: 1,
                    socketIoMode: SocketIoMode.NetworkStream);
                using var transport = new RawSocketTransport(pool, TlsBackend.SslStream);
                var proxySettings = new ProxySettings
                {
                    Address = new Uri("http://127.0.0.1:" + proxy.Port),
                    UseEnvironmentVariables = false
                };
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://origin.example.com/secure"))
                    .WithTimeout(RequestTimeout)
                    .WithMetadata(RequestMetadataKeys.ProxySettings, proxySettings);

                await AssertTimesOutAsync(transport, request);
                await AssertCompletesAsync(proxy.ClosedByClient(0), "proxy connection was not disposed on timeout");

                await AssertTimesOutAsync(transport, request);
                await AssertCompletesAsync(proxy.Accepted(1), "per-host permit was not released");
            });
        }

        [Test]
        public void Http2Preface_SilentServer_HonoursRequestCancellation()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new SilentServer();
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, server.Port);
                var stream = new NetworkStream(socket, ownsSocket: true);

                using var manager = new Http2ConnectionManager(new Http2Options(), new StreamingOptions());
                using var cts = new CancellationTokenSource(RequestTimeout);
                var create = manager.GetOrCreateAsync("h2.example.com", 443, stream, cts.Token).AsTask();

                // Shorter than Http2Connection.SettingsAckTimeout (5 s): the request token must win.
                var winner = await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(4)));
                Assert.AreSame(create, winner, "HTTP/2 preface wait outlived the request token");
                AssertAsync.ThrowsAsync<OperationCanceledException>(() => create);

                await AssertCompletesAsync(server.ClosedByClient(0), "h2 connection was not disposed on cancel");
                Assert.IsFalse(manager.HasConnection("h2.example.com", 443));
            });
        }

        private static async Task AssertTimesOutAsync(RawSocketTransport transport, UHttpRequest request)
        {
            var handler = new ErrorCapturingHandler();
            var dispatch = transport.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None);
            var winner = await Task.WhenAny(dispatch, Task.Delay(Guard));
            Assert.AreSame(dispatch, winner, "request outlived its timeout");
            await dispatch;

            Assert.IsNotNull(handler.Error, "the transport reported no error");
            Assert.AreEqual(UHttpErrorType.Timeout, handler.Error.HttpError.Type, handler.Error.ToString());
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }

        private sealed class ErrorCapturingHandler : IHttpHandler
        {
            public UHttpException Error { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                throw new AssertionException("A silent peer cannot produce a response (status " + statusCode + ").");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }

        /// <summary>Accepts connections, reads whatever arrives, never writes.</summary>
        private sealed class SilentServer : IDisposable
        {
            private const int MaxClients = 4;
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _gate = new object();
            private readonly TaskCompletionSource<bool>[] _accepted = NewSignals();
            private readonly TaskCompletionSource<bool>[] _closed = NewSignals();

            public SilentServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port { get; }

            public Task Accepted(int index) => _accepted[index].Task;

            /// <summary>Completes when the client side closes connection <paramref name="index"/>.</summary>
            public Task ClosedByClient(int index) => _closed[index].Task;

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
                lock (_gate)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }
                }
            }

            private static TaskCompletionSource<bool>[] NewSignals()
            {
                var signals = new TaskCompletionSource<bool>[MaxClients];
                for (var i = 0; i < signals.Length; i++)
                    signals[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return signals;
            }

            private async Task AcceptLoopAsync()
            {
                for (var index = 0; index < MaxClients; index++)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    lock (_gate)
                        _clients.Add(client);
                    _accepted[index].TrySetResult(true);
                    _ = ReadUntilClosedAsync(client, _closed[index]);
                }
            }

            private static async Task ReadUntilClosedAsync(TcpClient client, TaskCompletionSource<bool> closed)
            {
                var buffer = new byte[1024];
                try
                {
                    var stream = client.GetStream();
                    while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }
                catch (Exception)
                {
                    // Reset by the client also counts as closed.
                }

                closed.TrySetResult(true);
            }
        }
    }
}
