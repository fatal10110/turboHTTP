using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    [TestFixture]
    public class RawSocketTransportTests
    {
        private sealed class HttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private readonly Func<TcpClient, int, Task> _handler;
            private int _acceptCount;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public HttpServer(Func<TcpClient, int, Task> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
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
                    {
                        _clients.Add(client);
                    }

                    int index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => _handler(client, index));
                }
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

        private sealed class FailingStream : Stream
        {
            private readonly Stream _inner;

            public FailingStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new IOException("Injected failure");
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromException<int>(new IOException("Injected failure"));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new IOException("Injected failure");
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromException(new IOException("Injected failure"));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class NonSeekableReadStream : Stream
        {
            private readonly Stream _inner;

            public NonSeekableReadStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }

        [Test]
        public void SendAsync_UnsupportedTransferEncoding_MapsToNetworkError()        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\n" +
                                   "Transfer-Encoding: gzip\r\n" +
                                   "Content-Type: text/plain\r\n" +
                                   "\r\n" +
                                   "Hello";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        [Test]
        public void DispatchAsync_HandlerCallbackFailure_IsReportedViaOnResponseError()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\n" +
                                   "Content-Length: 5\r\n" +
                                   "Content-Type: text/plain\r\n" +
                                   "\r\n" +
                                   "Hello";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                using var transport = new RawSocketTransport();
                var request = new UHttpRequest(HttpMethod.GET, new Uri($"http://127.0.0.1:{server.Port}/"));
                var context = new RequestContext(request);
                var handler = new FailingResponseStartHandler();

                await transport.DispatchAsync(request, handler, context, CancellationToken.None);

                Assert.IsTrue(handler.ResponseErrorCalled);
                Assert.IsNotNull(handler.LastError);
                Assert.That(handler.LastError.Message, Does.Contain("handler-start-failure"));
            });
        }

        [Test]
        public void DispatchAsync_HandlerTerminalFailure_DoesNotLeakLeaseOwnership()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);

                    if (index == 1)
                    {
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\nHello");
                        await Task.Delay(TimeSpan.FromMilliseconds(100));
                        client.Close();
                        return;
                    }

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);

                var request = new UHttpRequest(HttpMethod.GET, new Uri($"http://127.0.0.1:{server.Port}/"));
                var context = new RequestContext(request);
                var handler = new ThrowingTerminalHandler();

                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                    await transport.DispatchAsync(request, handler, context, CancellationToken.None));

                Assert.That(ex.Message, Does.Contain("handler-error-failure"));

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/follow-up")
                    .WithTimeout(TimeSpan.FromSeconds(2))
                    .SendBufferedAsync(cts.Token);

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StaleConnection_RetriesOnce()        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        // Keep open: this connection is injected as a failing reused connection.
                        return;
                    }

                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                // Inject a reused connection that will throw on write/read.
                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var responseResult = await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, responseResult.StatusCode);
                Assert.GreaterOrEqual(server.AcceptCount, 2);
            });
        }

        [Test]
        public void RawSocketTransport_StaleConnection_NonIdempotent_NoRetry()        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        return;
                    }

                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post($"http://127.0.0.1:{server.Port}/").WithBody("test").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_KnownLengthFactoryBody_WritesContentLengthAndBody()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;
                Dictionary<string, string> headers = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    headers = captured.Headers;

                    var bodyBytes = await ReadExactAsync(stream, int.Parse(headers["Content-Length"]));
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                const string payload = "phase22a-stream-upload";
                var response = await client.Post($"http://127.0.0.1:{server.Port}/upload")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)),
                        payload.Length)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("POST /upload HTTP/1.1", requestLine);
                Assert.AreEqual(payload.Length.ToString(), headers["Content-Length"]);
                Assert.IsFalse(headers.ContainsKey("Transfer-Encoding"));
                Assert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_UnknownLengthFactoryBody_UsesChunkedTransferEncoding()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;
                Dictionary<string, string> headers = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    headers = captured.Headers;

                    var bodyBytes = await ReadChunkedBodyAsync(stream);
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                const string payload = "chunked-upload";
                var response = await client.Post($"http://127.0.0.1:{server.Port}/chunked")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("POST /chunked HTTP/1.1", requestLine);
                Assert.AreEqual("chunked", headers["Transfer-Encoding"]);
                Assert.IsFalse(headers.ContainsKey("Content-Length"));
                Assert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_Interim100_SendsBodyAndReturnsFinalResponse()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await WriteResponseHeadAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                    var bodyBytes = await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 100
                        }),
                    DisposeTransport = true
                });

                const string payload = "phase22b1-continue";
                using var response = await client.Post($"http://127.0.0.1:{server.Port}/continue")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)),
                        payload.Length)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_FinalResponseBefore100_DoesNotConsumeBody()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                int trailingByteRead = int.MinValue;
                var bodyStream = new TrackingNonSeekableReadStream(Encoding.UTF8.GetBytes("one-shot-body"));

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 4\r\nConnection: close\r\n\r\nnope");

                    trailingByteRead = await TryReadSingleByteAsync(stream, TimeSpan.FromMilliseconds(200));
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 500
                        }),
                    DisposeTransport = true
                });

                using var response = await client.Post($"http://127.0.0.1:{server.Port}/reject")
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual("nope", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(0, bodyStream.BytesRead);
                Assert.LessOrEqual(trailingByteRead, 0);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_Interim103BeforeFinalResponse_DoesNotConsumeBody()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                int trailingByteRead = int.MinValue;
                using var bodyStream = new TrackingNonSeekableReadStream(Encoding.UTF8.GetBytes("one-shot-body"));

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await WriteResponseHeadAsync(
                        stream,
                        "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload; as=style\r\n\r\n");
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 4\r\nConnection: close\r\n\r\nnope");

                    trailingByteRead = await TryReadSingleByteAsync(stream, TimeSpan.FromMilliseconds(200));
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 500
                        }),
                    DisposeTransport = true
                });

                using var response = await client.Post($"http://127.0.0.1:{server.Port}/reject-after-103")
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual("nope", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(0, bodyStream.BytesRead);
                Assert.LessOrEqual(trailingByteRead, 0);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_KeepAliveRejectionWithBody_ReusesConnectionAfterDrain()
        {
            AssertAsync.Run(async () =>
            {
                string followUpRequestLine = null;
                using var bodyStream = new TrackingNonSeekableReadStream(Encoding.UTF8.GetBytes("reject-me"));

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        var first = await ReadRequestStartAndHeadersAsync(stream);
                        Assert.AreEqual("POST /reject HTTP/1.1", first.RequestLine);

                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 4\r\nConnection: keep-alive\r\n\r\nnope");

                        var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                        followUpRequestLine = followUp?.RequestLine;
                        if (followUp != null)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        }

                        client.Close();
                        return;
                    }

                    var fallback = await ReadRequestStartAndHeadersAsync(stream);
                    if (followUpRequestLine == null)
                        followUpRequestLine = fallback.RequestLine;

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                using (var rejectResponse = await client.Post($"http://127.0.0.1:{server.Port}/reject")
                           .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                           .WithExpectContinue()
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.ExpectationFailed, rejectResponse.StatusCode);
                    Assert.AreEqual("nope", rejectResponse.GetBodyAsString());
                }

                using var followUpResponse = await client.Get($"http://127.0.0.1:{server.Port}/follow-up")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUpResponse.StatusCode);
                Assert.AreEqual("ok", followUpResponse.GetBodyAsString());
                Assert.AreEqual(0, bodyStream.BytesRead);
                Assert.AreEqual("GET /follow-up HTTP/1.1", followUpRequestLine);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_Timeout_SendsBodyAnyway()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await Task.Delay(TimeSpan.FromMilliseconds(150));
                    var bodyBytes = await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 50
                        }),
                    DisposeTransport = true
                });

                const string payload = "timeout-upload";
                using var response = await client.Post($"http://127.0.0.1:{server.Port}/timeout")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)),
                        payload.Length)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_Timeout_Late100DuringSlowBody_CompletesWithoutByteLoss()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                byte[] requestBody = null;
                var payload = Encoding.ASCII.GetBytes(new string('x', 300));

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    var firstChunk = await ReadExactAsync(stream, 100);
                    await WriteResponseHeadAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");

                    var remaining = await ReadExactAsync(stream, payload.Length - firstChunk.Length);
                    requestBody = new byte[payload.Length];
                    Buffer.BlockCopy(firstChunk, 0, requestBody, 0, firstChunk.Length);
                    Buffer.BlockCopy(remaining, 0, requestBody, firstChunk.Length, remaining.Length);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndone");
                    client.Close();
                });

                using var bodyStream = new DelayedChunkReadStream(
                    payload,
                    chunkSize: 100,
                    delayPerRead: TimeSpan.FromMilliseconds(100));
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 50
                        }),
                    DisposeTransport = true
                });

                using var response = await client.Post($"http://127.0.0.1:{server.Port}/slow-timeout")
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("done", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(payload.Length, bodyStream.BytesRead);
                Assert.AreEqual(3, bodyStream.ReadCount);
                CollectionAssert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_Timeout_Late103DuringSlowBody_CompletesFinalResponse()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                byte[] requestBody = null;
                var payload = Encoding.ASCII.GetBytes(new string('y', 300));

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await Task.Delay(TimeSpan.FromMilliseconds(80));
                    await WriteResponseHeadAsync(
                        stream,
                        "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload; as=style\r\n\r\n");

                    requestBody = await ReadExactAsync(stream, payload.Length);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndone");
                    client.Close();
                });

                using var bodyStream = new DelayedChunkReadStream(
                    payload,
                    chunkSize: 100,
                    delayPerRead: TimeSpan.FromMilliseconds(100));
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 50
                        }),
                    DisposeTransport = true
                });

                using var response = await client.Post($"http://127.0.0.1:{server.Port}/late-103")
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("done", response.GetBodyAsString());
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual(payload.Length, bodyStream.BytesRead);
                Assert.AreEqual(3, bodyStream.ReadCount);
                CollectionAssert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_ExpectContinue_BodySendFailureAfterContinue_ThrowsTransportError()
        {
            AssertAsync.Run(async () =>
            {
                const int bytesBeforeFailure = 5;
                int bytesReceived = 0;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestStartAndHeadersAsync(stream);
                    await WriteResponseHeadAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");

                    var partial = await ReadExactAsync(stream, bytesBeforeFailure);
                    bytesReceived = partial.Length;

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var bodyStream = new FailingAfterBytesReadStream(
                    Encoding.ASCII.GetBytes("abcdefghij"),
                    bytesBeforeFailure,
                    new IOException("body-send-failure"));
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            ExpectContinueTimeoutMs = 100
                        }),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post($"http://127.0.0.1:{server.Port}/failing-body")
                        .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                        .WithExpectContinue()
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.IsInstanceOf<IOException>(ex.HttpError.InnerException);
                Assert.AreEqual("body-send-failure", ex.HttpError.InnerException.Message);
                Assert.AreEqual(bytesBeforeFailure, bytesReceived);
                Assert.AreEqual(bytesBeforeFailure, bodyStream.BytesRead);
            });
        }

        [Test]
        public void RawSocketTransport_AutoExpectContinueThreshold_InjectsHeaderBeforeHandlerCallbacks()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    await WriteResponseHeadAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                    var bodyBytes = await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                using var transport = new RawSocketTransport(
                    pool: null,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: new StreamingOptions
                    {
                        AutoExpectContinueThresholdBytes = 4,
                        ExpectContinueTimeoutMs = 100
                    });

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri($"http://127.0.0.1:{server.Port}/auto"),
                    body: Encoding.UTF8.GetBytes("payload"));
                var context = new RequestContext(request);
                var handler = new CapturingRequestStartHandler();

                await transport.DispatchAsync(request, handler, context, CancellationToken.None);

                Assert.AreEqual("100-continue", handler.RequestExpectHeader);
                Assert.AreEqual("100-continue", context.Request.Headers.Get("Expect"));
                Assert.AreEqual("100-continue", expectHeader);
                Assert.AreEqual("payload", requestBody);
                Assert.AreEqual(200, handler.ResponseStatusCode);
            });
        }

        [Test]
        public void RawSocketTransport_AutoExpectContinueThreshold_UnknownLengthBody_DoesNotInjectHeader()
        {
            AssertAsync.Run(async () =>
            {
                string expectHeader = null;
                string requestBody = null;

                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Expect", out expectHeader);

                    var bodyBytes = await ReadChunkedBodyAsync(stream);
                    requestBody = Encoding.UTF8.GetString(bodyBytes);

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(
                        pool: null,
                        tlsBackend: TlsBackend.Auto,
                        http2Options: new Http2Options(),
                        streamingOptions: new StreamingOptions
                        {
                            AutoExpectContinueThresholdBytes = 1,
                            ExpectContinueTimeoutMs = 50
                        }),
                    DisposeTransport = true
                });

                const string payload = "chunked-auto-threshold";
                using var response = await client.Post($"http://127.0.0.1:{server.Port}/chunked-auto")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsNull(expectHeader);
                Assert.AreEqual(payload, requestBody);
            });
        }

        [Test]
        public void RawSocketTransport_StaleConnection_NonReplayableKnownLengthBody_NoRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        return;
                    }

                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var bodyBytes = Encoding.UTF8.GetBytes("one-shot");
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/")
                        .WithStreamBody(
                            new NonSeekableReadStream(new MemoryStream(bodyBytes, writable: false)),
                            bodyBytes.Length)
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StaleConnection_ReplayablePostBody_RetriesWhenNoBodyBytesCommitted()
        {
            AssertAsync.Run(async () =>
            {
                string requestBody = null;

                using var server = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        return;
                    }

                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    var bodyBytes = await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));
                    requestBody = Encoding.UTF8.GetString(bodyBytes);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(failingClient.Client, failingStream, "127.0.0.1", server.Port, false);

                var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = $"127.0.0.1:{server.Port}:";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                const string payload = "retry-before-body-commit";
                var response = await client.Post($"http://127.0.0.1:{server.Port}/")
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false)),
                        payload.Length)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(payload, requestBody);
                Assert.GreaterOrEqual(server.AcceptCount, 2);
            });
        }

        [Test]
        public void RawSocketTransport_ReusedConnection_ReplayablePostBody_PartialSendFailure_DoesNotRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index != 1)
                    {
                        var retryRequest = await ReadRequestStartAndHeadersAsync(stream);
                        var retryLength = int.Parse(retryRequest.Headers["Content-Length"]);
                        await ReadExactAsync(stream, retryLength);
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        client.Close();
                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                    var secondRequest = await ReadRequestStartAndHeadersAsync(stream);
                    var secondLength = int.Parse(secondRequest.Headers["Content-Length"]);
                    Assert.Greater(secondLength, 1);
                    await ReadExactAsync(stream, 1);

                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var warmup = await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, warmup.StatusCode);

                var payload = new byte[256 * 1024];
                Array.Fill(payload, (byte)'x');

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post($"http://127.0.0.1:{server.Port}/")
                        .WithBodyFactory(
                            _ => new ValueTask<Stream>(new MemoryStream(payload, writable: false)),
                            payload.Length)
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_NonReplayableBody_PartialSendFailure_UsesDedicatedError()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index != 1)
                    {
                        var retryRequest = await ReadRequestStartAndHeadersAsync(stream);
                        var retryLength = int.Parse(retryRequest.Headers["Content-Length"]);
                        await ReadExactAsync(stream, retryLength);
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        client.Close();
                        return;
                    }

                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    var contentLength = int.Parse(captured.Headers["Content-Length"]);
                    Assert.Greater(contentLength, 1);
                    await ReadExactAsync(stream, 1);

                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var payload = new byte[256 * 1024];
                Array.Fill(payload, (byte)'y');

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post($"http://127.0.0.1:{server.Port}/")
                        .WithStreamBody(
                            new NonSeekableReadStream(new MemoryStream(payload, writable: false)),
                            payload.Length)
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("non-replayable", ex.HttpError.Message);
                StringAssert.Contains("cannot be retried", ex.HttpError.Message);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingContentLength_EarlyDisposeWithinBudget_ReusesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        await ReadRequestHeadersAsync(stream);
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: keep-alive\r\n\r\nHelloWorld");

                        var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                        sameConnectionFollowUp = followUp?.RequestLine;
                        if (followUp != null)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        }

                        client.Close();
                        return;
                    }

                    var fallback = await ReadRequestStartAndHeadersAsync(stream);
                    sameConnectionFollowUp = fallback.RequestLine;
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/first").SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    Assert.AreEqual((byte)'H', buffer[0]);
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual("GET /second HTTP/1.1", sameConnectionFollowUp);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingContentLength_EarlyDisposeOverBudget_ClosesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;
                var largeBody = new byte[(64 * 1024) + 2];
                Array.Fill(largeBody, (byte)'a');

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        try
                        {
                            await ReadRequestHeadersAsync(stream);
                            await WriteResponseAsync(
                                stream,
                                $"HTTP/1.1 200 OK\r\nContent-Length: {largeBody.Length}\r\nConnection: keep-alive\r\n\r\n",
                                largeBody);

                            var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromMilliseconds(300));
                            sameConnectionFollowUp = followUp?.RequestLine;
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }
                        finally
                        {
                            client.Close();
                        }

                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/large").SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.IsNull(sameConnectionFollowUp);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingChunked_EarlyDisposeWithinBudget_ReusesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        await ReadRequestHeadersAsync(stream);
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n" +
                            "1\r\nA\r\n" +
                            "4\r\nBCDE\r\n" +
                            "0\r\nX-Trailer: yes\r\n\r\n");

                        var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                        sameConnectionFollowUp = followUp?.RequestLine;
                        if (followUp != null)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        }

                        client.Close();
                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/chunked").SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    Assert.AreEqual((byte)'A', buffer[0]);
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual("GET /second HTTP/1.1", sameConnectionFollowUp);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingHeadResponse_UsesEmptyBodyAndReusesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        var first = await ReadRequestStartAndHeadersAsync(stream);
                        if (string.Equals(first.RequestLine, "HEAD /head HTTP/1.1", StringComparison.Ordinal))
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n");
                        }

                        var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                        sameConnectionFollowUp = followUp?.RequestLine;
                        if (followUp != null)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        }

                        client.Close();
                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Head($"http://127.0.0.1:{server.Port}/head").SendStreamingAsync())
                {
                    Assert.AreEqual(0, response.Body.Length);
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(0, read);
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual("GET /second HTTP/1.1", sameConnectionFollowUp);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingReadToEnd_DisposeClosesConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        await ReadRequestHeadersAsync(stream);
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\n\r\nHelloWorld");
                        client.Close();
                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/read-to-end").SendStreamingAsync())
                {
                    Assert.Throws<NotSupportedException>(() => _ = response.Body.Length);
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingBodyRead_IgnoresRequestTimeoutAfterHeaders()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);

                    await WriteResponseHeadAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n");

                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("Hello"), 0, 5);
                    await stream.FlushAsync();
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using var response = await client.Get($"http://127.0.0.1:{server.Port}/timeout")
                    .WithTimeout(TimeSpan.FromMilliseconds(100))
                    .SendStreamingAsync();

                var buffer = new byte[5];
                var read = await response.Body.ReadAsync(buffer, 0, buffer.Length);

                Assert.AreEqual(5, read);
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(buffer, 0, read));
            });
        }

        [Test]
        public void RawSocketTransport_BufferedBodyRead_StillRespectsRequestTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);

                    await WriteResponseHeadAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n");

                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("Hello"), 0, 5);
                    await stream.FlushAsync();
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/timeout")
                        .WithTimeout(TimeSpan.FromMilliseconds(100))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingConnectionClose_EarlyDispose_SkipsDrainAndCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        await ReadRequestHeadersAsync(stream);
                        await WriteResponseHeadAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("H"), 0, 1);
                        await stream.FlushAsync();

                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                        try
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("ello"), 0, 4);
                            await stream.FlushAsync();
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }

                        client.Close();
                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/close").SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);

                    var stopwatch = Stopwatch.StartNew();
                    await response.DisposeAsync();
                    stopwatch.Stop();

                    Assert.Less(stopwatch.Elapsed, TimeSpan.FromMilliseconds(1000));
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void RawSocketTransport_StreamingBodyRead_UserCancellation_ClosesConnection()
        {
            AssertAsync.Run(async () =>
            {
                string sameConnectionFollowUp = null;

                using var server = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        try
                        {
                            await ReadRequestHeadersAsync(stream);
                            await WriteResponseHeadAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n");

                            await Task.Delay(TimeSpan.FromMilliseconds(250));
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("Hello"), 0, 5);
                            await stream.FlushAsync();

                            var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromMilliseconds(300));
                            sameConnectionFollowUp = followUp?.RequestLine;
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }
                        finally
                        {
                            client.Close();
                        }

                        return;
                    }

                    await ReadRequestHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                await using (var response = await client.Get($"http://127.0.0.1:{server.Port}/cancel").SendStreamingAsync())
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    var buffer = new byte[5];

                    var ex = AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                        await response.Body.ReadAsync(buffer, 0, buffer.Length, cts.Token));

                    Assert.AreEqual(cts.Token, ex.CancellationToken);
                    await response.DisposeAsync();
                }

                using var followUp = await client.Get($"http://127.0.0.1:{server.Port}/second").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("ok", followUp.GetBodyAsString());
                Assert.IsNull(sameConnectionFollowUp);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void NonKeepAlive_Response_SemaphoreReleased()        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestHeadersAsync(stream);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var first = await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync();
                var second = await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            });
        }

        [Test]
        public void HttpViaProxy_UsesAbsoluteForm()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;
                string proxyAuth = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    captured.Headers.TryGetValue("Proxy-Authorization", out proxyAuth);

                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                var response = await client.Get("http://origin.example.com/resource?id=42").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("GET http://origin.example.com/resource?id=42 HTTP/1.1", requestLine);
                Assert.IsNull(proxyAuth);
            });
        }

        [Test]
        public void HttpViaProxy_DisallowPlaintextProxyAuth_StripsSuppliedProxyAuthorization()
        {
            AssertAsync.Run(async () =>
            {
                string proxyAuth = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    captured.Headers.TryGetValue("Proxy-Authorization", out proxyAuth);

                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = false,
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var response = await client
                    .Get("http://origin.example.com/resource?id=42")
                    .WithHeader("Proxy-Authorization", "Basic should-not-leak")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.IsNull(proxyAuth);
            });
        }

        [Test]
        public void HttpViaProxy_StreamingRequestBody_UsesAbsoluteFormAndStreamsContent()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;
                byte[] requestBody = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    requestBody = await ReadExactAsync(stream, int.Parse(captured.Headers["Content-Length"]));

                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var bodyStream = new TrackingNonSeekableReadStream(Encoding.UTF8.GetBytes("stream body"));
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var response = await client
                    .Post("http://origin.example.com/upload?x=1")
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("POST http://origin.example.com/upload?x=1 HTTP/1.1", requestLine);
                Assert.AreEqual("stream body", Encoding.UTF8.GetString(requestBody));
                Assert.AreEqual(bodyStream.Length, bodyStream.BytesRead);
            });
        }

        [Test]
        public void HttpViaProxy_StreamingResponse_Dispose_ReusesProxyConnection()
        {
            AssertAsync.Run(async () =>
            {
                string firstRequestLine = null;
                string sameConnectionFollowUp = null;

                using var proxyServer = new HttpServer(async (client, index) =>
                {
                    using var stream = client.GetStream();

                    if (index == 1)
                    {
                        var first = await ReadRequestStartAndHeadersAsync(stream);
                        firstRequestLine = first.RequestLine;

                        await WriteResponseHeadAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("Hello"), 0, 5);
                        await stream.FlushAsync();

                        var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(2));
                        sameConnectionFollowUp = followUp?.RequestLine;
                        if (followUp != null)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        }

                        client.Close();
                        return;
                    }

                    var fallback = await ReadRequestStartAndHeadersAsync(stream);
                    if (sameConnectionFollowUp == null)
                        sameConnectionFollowUp = fallback.RequestLine;

                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                await using (var response = await client
                                 .Get("http://origin.example.com/first")
                                 .SendStreamingAsync())
                {
                    var buffer = new byte[1];
                    var read = await response.Body.ReadAsync(buffer, 0, 1);
                    Assert.AreEqual(1, read);
                    await response.DisposeAsync();
                }

                using var followUpResponse = await client
                    .Get("http://second.example.com/second")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUpResponse.StatusCode);
                Assert.AreEqual("ok", followUpResponse.GetBodyAsString());
                Assert.AreEqual("GET http://origin.example.com/first HTTP/1.1", firstRequestLine);
                Assert.AreEqual("GET http://second.example.com/second HTTP/1.1", sameConnectionFollowUp);
                Assert.AreEqual(1, proxyServer.AcceptCount);
            });
        }

        [Test]
        public void HttpViaProxy_StaleProxyConnection_Idempotent_Retries()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;

                using var proxyServer = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        // Keep open so the injected pooled connection stays "alive" until the
                        // failing stream triggers the stale-retry path in the transport.
                        return;
                    }

                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    client.Close();
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, proxyServer.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(
                    failingClient.Client,
                    failingStream,
                    "127.0.0.1",
                    proxyServer.Port,
                    false);

                var idleField = typeof(TcpConnectionPool).GetField(
                    "_idleConnections",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = "127.0.0.1:" + proxyServer.Port + ":";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var response = await client
                    .Get("http://origin.example.com/retry")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("GET http://origin.example.com/retry HTTP/1.1", requestLine);
                Assert.GreaterOrEqual(proxyServer.AcceptCount, 2);
            });
        }

        [Test]
        public void HttpViaProxy_StaleProxyConnection_NonIdempotent_NoRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var proxyServer = new HttpServer(async (client, index) =>
                {
                    if (index == 1)
                    {
                        return;
                    }

                    using var stream = client.GetStream();
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                });

                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var transport = new RawSocketTransport(pool);

                var failingClient = new TcpClient();
                await failingClient.ConnectAsync(IPAddress.Loopback, proxyServer.Port);
                var failingStream = new FailingStream(failingClient.GetStream());
                var failingConn = new PooledConnection(
                    failingClient.Client,
                    failingStream,
                    "127.0.0.1",
                    proxyServer.Port,
                    false);

                var idleField = typeof(TcpConnectionPool).GetField(
                    "_idleConnections",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
                var key = "127.0.0.1:" + proxyServer.Port + ":";
                var queue = idle.GetOrAdd(key, _ => new ConcurrentQueue<PooledConnection>());
                queue.Enqueue(failingConn);

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client
                        .Post("http://origin.example.com/retry")
                        .WithBody("test")
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, proxyServer.AcceptCount);
            });
        }

        [Test]
        public void HttpViaProxy_BufferedChunkedResponse_PreservesTrailers()
        {
            AssertAsync.Run(async () =>
            {
                string requestLine = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    var captured = await ReadRequestStartAndHeadersAsync(stream);
                    requestLine = captured.RequestLine;

                    var response =
                        "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                        "2\r\nok\r\n" +
                        "0\r\nX-Trailer: yes\r\n\r\n";
                    await WriteResponseAsync(stream, response);
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var response = await client
                    .Get("http://origin.example.com/resource?id=42")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                Assert.AreEqual("yes", response.Trailers.Get("X-Trailer"));
                Assert.AreEqual("GET http://origin.example.com/resource?id=42 HTTP/1.1", requestLine);
            });
        }

        [Test]
        public void Connect407_RetryWithAuthOnce()
        {
            AssertAsync.Run(async () =>
            {
                int connectCount = 0;
                string firstConnection = null;
                string secondConnection = null;
                string firstProxyConnection = null;
                string secondProxyConnection = null;
                string firstAuth = null;
                string secondAuth = null;

                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();

                    var first = await ReadRequestStartAndHeadersAsync(stream);
                    connectCount++;
                    first.Headers.TryGetValue("Connection", out firstConnection);
                    first.Headers.TryGetValue("Proxy-Connection", out firstProxyConnection);
                    first.Headers.TryGetValue("Proxy-Authorization", out firstAuth);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                    var second = await ReadRequestStartAndHeadersAsync(stream);
                    connectCount++;
                    second.Headers.TryGetValue("Connection", out secondConnection);
                    second.Headers.TryGetValue("Proxy-Connection", out secondProxyConnection);
                    second.Headers.TryGetValue("Proxy-Authorization", out secondAuth);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 200 Connection Established\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    Credentials = new NetworkCredential("user", "pass"),
                    AllowPlaintextProxyAuth = true,
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                // CONNECT succeeds but TLS handshake cannot complete with this plain test server.
                AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync());

                Assert.AreEqual(2, connectCount);
                Assert.AreEqual("keep-alive", firstConnection);
                Assert.AreEqual("keep-alive", secondConnection);
                Assert.IsNull(firstProxyConnection);
                Assert.IsNull(secondProxyConnection);
                Assert.IsNull(firstAuth);
                Assert.IsNotNull(secondAuth);
                StringAssert.StartsWith("Basic ", secondAuth);
            });
        }

        [Test]
        public void Connect407_ChunkedBodyDrain_PreservesNextConnectResponse()
        {
            AssertAsync.Run(async () =>
            {
                var responseBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                    "Transfer-Encoding: chunked\r\n" +
                    "Connection: keep-alive\r\n\r\n" +
                    "3\r\nabc\r\n" +
                    "0\r\n" +
                    "Proxy-Authenticate: Basic realm=\"proxy\"\r\n\r\n" +
                    "HTTP/1.1 200 Connection Established\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n");

                using var stream = new MemoryStream(responseBytes, writable: false);

                var firstResponse = await ReadProxyConnectResponseForTestAsync(stream);
                Assert.AreEqual(407, GetProxyConnectStatusCode(firstResponse));

                await DrainProxyConnectBodyForTestAsync(
                    stream,
                    GetProxyConnectHeaders(firstResponse));

                var secondResponse = await ReadProxyConnectResponseForTestAsync(stream);
                Assert.AreEqual(200, GetProxyConnectStatusCode(secondResponse));
            });
        }

        [Test]
        public void Connect407_NoCredentialsFails()
        {
            AssertAsync.Run(async () =>
            {
                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestStartAndHeadersAsync(stream);
                    await WriteResponseAsync(
                        stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync());
                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Proxy authentication required", ex.HttpError.Message);
            });
        }

        [Test]
        public void CancellationDuringConnect_NoLeaks()
        {
            AssertAsync.Run(async () =>
            {
                using var proxyServer = new HttpServer(async (client, _) =>
                {
                    using var stream = client.GetStream();
                    await ReadRequestStartAndHeadersAsync(stream);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    client.Close();
                });

                var proxySettings = new ProxySettings
                {
                    Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                    UseEnvironmentVariables = false
                };

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    Proxy = proxySettings
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                    await client.Get("https://origin.example.com/secure").SendBufferedAsync(cts.Token));
            });
        }

        private static async Task<object> ReadProxyConnectResponseForTestAsync(Stream stream)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "ReadProxyConnectResponseAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var task = (Task)method.Invoke(null, new object[] { stream, CancellationToken.None });
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        private static async Task DrainProxyConnectBodyForTestAsync(Stream stream, HttpHeaders headers)
        {
            var method = typeof(RawSocketTransport).GetMethod(
                "DrainProxyConnectBodyAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var task = (Task)method.Invoke(null, new object[] { stream, headers, CancellationToken.None });
            await task.ConfigureAwait(false);
        }

        private static int GetProxyConnectStatusCode(object response)
        {
            Assert.IsNotNull(response);
            var property = response.GetType().GetProperty("StatusCode", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property);
            return (int)property.GetValue(response, null);
        }

        private static HttpHeaders GetProxyConnectHeaders(object response)
        {
            Assert.IsNotNull(response);
            var property = response.GetType().GetProperty("Headers", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property);
            return (HttpHeaders)property.GetValue(response, null);
        }

#if TURBOHTTP_INTEGRATION_TESTS
        [Test]
        [Category("Integration")]
        public void RawSocketTransport_FreshConnection_IOException_NoRetry()        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer((client, _) =>
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                    return Task.CompletedTask;
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get($"http://127.0.0.1:{server.Port}/").SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }
#endif

        private static async Task WriteResponseAsync(NetworkStream stream, string response)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private static async Task WriteResponseAsync(NetworkStream stream, string responseHead, byte[] body)
        {
            await WriteResponseHeadAsync(stream, responseHead);
            if (body != null && body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length);
                await stream.FlushAsync();
            }
        }

        private static async Task WriteResponseHeadAsync(NetworkStream stream, string responseHead)
        {
            var bytes = Encoding.ASCII.GetBytes(responseHead);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private static async Task ReadRequestHeadersAsync(NetworkStream stream)
        {
            var buffer = new byte[1];
            int matched = 0; // matches \r\n\r\n

            while (matched < 4)
            {
                int read = await stream.ReadAsync(buffer, 0, 1);
                if (read == 0) break;

                byte b = buffer[0];
                if (matched == 0 && b == (byte)'\r') matched = 1;
                else if (matched == 1 && b == (byte)'\n') matched = 2;
                else if (matched == 2 && b == (byte)'\r') matched = 3;
                else if (matched == 3 && b == (byte)'\n') matched = 4;
                else matched = b == (byte)'\r' ? 1 : 0;
            }
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)> ReadRequestStartAndHeadersAsync(NetworkStream stream)
        {
            return await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)> ReadRequestStartAndHeadersAsync(
            NetworkStream stream,
            CancellationToken ct)
        {
            var requestLine = await ReadLineAsync(stream, ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                var line = await ReadLineAsync(stream, ct);
                if (string.IsNullOrEmpty(line))
                    break;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return (requestLine, headers);
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers)?> TryReadRequestStartAndHeadersAsync(
            NetworkStream stream,
            TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var request = await ReadRequestStartAndHeadersAsync(stream, cts.Token);
                if (string.IsNullOrEmpty(request.RequestLine))
                    return null;

                return request;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int byteCount)
        {
            var buffer = new byte[byteCount];
            int offset = 0;
            while (offset < byteCount)
            {
                int read = await stream.ReadAsync(buffer, offset, byteCount - offset);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected EOF while reading request body.");

                offset += read;
            }

            return buffer;
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream);
                int chunkSize = int.Parse(sizeLine, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                if (chunkSize == 0)
                {
                    await ReadLineAsync(stream);
                    break;
                }

                var chunk = await ReadExactAsync(stream, chunkSize);
                await body.WriteAsync(chunk, 0, chunk.Length);
                var chunkTerminator = await ReadLineAsync(stream);
                Assert.AreEqual(string.Empty, chunkTerminator);
            }

            return body.ToArray();
        }

        private static async Task<int> TryReadSingleByteAsync(NetworkStream stream, TimeSpan timeout)
        {
            var buffer = new byte[1];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await stream.ReadAsync(buffer, 0, 1, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return -1;
            }
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream)
        {
            return await ReadLineAsync(stream, CancellationToken.None);
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>(128);
            var buffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, 1, ct);
                if (read == 0)
                    break;

                if (buffer[0] == (byte)'\n')
                    break;
                if (buffer[0] != (byte)'\r')
                    bytes.Add(buffer[0]);
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private sealed class FailingResponseStartHandler : IHttpHandler
        {
            public bool ResponseErrorCalled { get; private set; }
            public UHttpException LastError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                throw new InvalidOperationException("handler-start-failure");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                ResponseErrorCalled = true;
                LastError = error;
            }
        }

        private sealed class CapturingRequestStartHandler : IHttpHandler
        {
            public string RequestExpectHeader { get; private set; }
            public int ResponseStatusCode { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                RequestExpectHeader = request.Headers.Get("Expect");
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                ResponseStatusCode = statusCode;
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                throw new AssertionException("Did not expect OnResponseError: " + error?.Message);
            }
        }

        private sealed class ThrowingTerminalHandler : IHttpHandler
        {
            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                throw new InvalidOperationException("handler-start-failure");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                throw new InvalidOperationException("handler-error-failure");
            }
        }

        private sealed class TrackingNonSeekableReadStream : Stream
        {
            private readonly MemoryStream _inner;

            public TrackingNonSeekableReadStream(byte[] bytes)
            {
                _inner = new MemoryStream(bytes ?? Array.Empty<byte>(), writable: false);
            }

            public int BytesRead { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                BytesRead += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class DelayedChunkReadStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly int _chunkSize;
            private readonly TimeSpan _delayPerRead;
            private int _position;

            public DelayedChunkReadStream(byte[] bytes, int chunkSize, TimeSpan delayPerRead)
            {
                _bytes = bytes ?? Array.Empty<byte>();
                _chunkSize = chunkSize <= 0 ? throw new ArgumentOutOfRangeException(nameof(chunkSize)) : chunkSize;
                _delayPerRead = delayPerRead;
            }

            public int BytesRead { get; private set; }
            public int ReadCount { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _bytes.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _bytes.Length)
                    return 0;

                if (_delayPerRead > TimeSpan.Zero)
                    Task.Delay(_delayPerRead).GetAwaiter().GetResult();

                int bytesToCopy = Math.Min(Math.Min(count, _chunkSize), _bytes.Length - _position);
                Buffer.BlockCopy(_bytes, _position, buffer, offset, bytesToCopy);
                _position += bytesToCopy;
                BytesRead += bytesToCopy;
                ReadCount++;
                return bytesToCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= _bytes.Length)
                    return 0;

                if (_delayPerRead > TimeSpan.Zero)
                    await Task.Delay(_delayPerRead, cancellationToken).ConfigureAwait(false);

                int bytesToCopy = Math.Min(Math.Min(buffer.Length, _chunkSize), _bytes.Length - _position);
                _bytes.AsMemory(_position, bytesToCopy).CopyTo(buffer);
                _position += bytesToCopy;
                BytesRead += bytesToCopy;
                ReadCount++;
                return bytesToCopy;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class FailingAfterBytesReadStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly int _throwAfterBytes;
            private readonly Exception _failure;
            private int _position;

            public FailingAfterBytesReadStream(byte[] bytes, int throwAfterBytes, Exception failure)
            {
                _bytes = bytes ?? Array.Empty<byte>();
                _throwAfterBytes = throwAfterBytes;
                _failure = failure ?? throw new ArgumentNullException(nameof(failure));
            }

            public int BytesRead { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _bytes.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _bytes.Length)
                    return 0;
                if (_position >= _throwAfterBytes)
                    throw _failure;

                int bytesToCopy = Math.Min(Math.Min(count, _throwAfterBytes - _position), _bytes.Length - _position);
                Buffer.BlockCopy(_bytes, _position, buffer, offset, bytesToCopy);
                _position += bytesToCopy;
                BytesRead += bytesToCopy;
                return bytesToCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= _bytes.Length)
                    return new ValueTask<int>(0);
                if (_position >= _throwAfterBytes)
                    return new ValueTask<int>(Task.FromException<int>(_failure));

                int bytesToCopy = Math.Min(Math.Min(buffer.Length, _throwAfterBytes - _position), _bytes.Length - _position);
                _bytes.AsMemory(_position, bytesToCopy).CopyTo(buffer);
                _position += bytesToCopy;
                BytesRead += bytesToCopy;
                return new ValueTask<int>(bytesToCopy);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
