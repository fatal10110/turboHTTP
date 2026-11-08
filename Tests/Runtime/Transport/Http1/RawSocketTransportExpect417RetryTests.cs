using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// RFC 9110 Section 10.1.1 (#214): a 417 to an Expect: 100-continue request whose body was not
    /// sent yet is repeated once without Expect, on another connection, when the body is replayable.
    /// A non-replayable body, another expectation, a body already sent, or a second 417 return the 417.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportExpect417RetryTests
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
        private const string Body = "replayable";

        private const string Rejection =
            "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 4\r\nConnection: keep-alive\r\n\r\nnope";

        [Test]
        public void Expect417_RetriesWithoutExpect_ReplayableBody()
        {
            AssertAsync.Run(async () =>
            {
                string firstHead = null;
                string retryHead = null;
                string retryBody = null;
                var rejectedAfter417 = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

                using var server = new LoopbackServer(async (stream, index) =>
                {
                    var head = await ReadHeadAsync(stream);
                    if (index == 1)
                    {
                        firstHead = head;
                        await WriteAsync(stream, Rejection);

                        // The rejected connection is closed: no body byte, no next request (#37).
                        var after = await ReadUpToAsync(stream, 1);
                        rejectedAfter417.TrySetResult(after.Length);
                        return;
                    }

                    retryHead = head;
                    retryBody = Encoding.ASCII.GetString(await ReadUpToAsync(stream, Body.Length));
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                });

                using var client = CreateClient(new StreamingOptions { ExpectContinueTimeoutMs = 30000 });

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(Body)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                StringAssert.Contains("Expect: 100-continue", firstHead);
                Assert.AreEqual("POST /upload HTTP/1.1", FirstLine(retryHead));
                StringAssert.DoesNotContain("Expect:", retryHead);
                Assert.AreEqual(Body, retryBody);
                Assert.AreEqual(0, await WithGuard(rejectedAfter417.Task));
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void Expect417_AutoInjectedExpect_RetriesWithoutExpect()
        {
            AssertAsync.Run(async () =>
            {
                string firstHead = null;
                string retryHead = null;
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    var head = await ReadHeadAsync(stream);
                    if (index == 1)
                    {
                        firstHead = head;
                        await WriteAsync(stream, Rejection);
                        return;
                    }

                    retryHead = head;
                    await ReadUpToAsync(stream, Body.Length);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                using var client = CreateClient(new StreamingOptions
                {
                    ExpectContinueTimeoutMs = 30000,
                    AutoExpectContinueThresholdBytes = 4
                });

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(Body)
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                StringAssert.Contains("Expect: 100-continue", firstHead);
                StringAssert.DoesNotContain("Expect:", retryHead);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void Expect417_NonReplayableBody_ReturnsExpectationFailed()
        {
            AssertAsync.Run(async () =>
            {
                string firstHead = null;
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    firstHead = await ReadHeadAsync(stream);
                    await WriteAsync(stream, Rejection);
                });

                using var bodyStream = new NonSeekableStream(Encoding.ASCII.GetBytes(Body));
                using var client = CreateClient(new StreamingOptions { ExpectContinueTimeoutMs = 30000 });

                using var response = await client.Post(server.Url("/upload"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual("nope", response.GetBodyAsString());
                StringAssert.Contains("Expect: 100-continue", firstHead);
                Assert.AreEqual(0, bodyStream.BytesRead);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void Expect417_SecondExpectationFailed_ReturnedWithoutThirdAttempt()
        {
            AssertAsync.Run(async () =>
            {
                var heads = new List<string>();
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    var head = await ReadHeadAsync(stream);
                    lock (heads)
                        heads.Add(head);
                    if (index > 1)
                        await ReadUpToAsync(stream, Body.Length);
                    await WriteAsync(stream,
                        "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 6\r\nConnection: close\r\n\r\nnope-" + index);
                });

                using var client = CreateClient(new StreamingOptions { ExpectContinueTimeoutMs = 30000 });

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(Body)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual("nope-2", response.GetBodyAsString());
                Assert.AreEqual(2, server.AcceptCount);
                lock (heads)
                {
                    Assert.AreEqual(2, heads.Count);
                    StringAssert.Contains("Expect: 100-continue", heads[0]);
                    StringAssert.DoesNotContain("Expect:", heads[1]);
                }
            });
        }

        [Test]
        public void Expect417_OtherExpectationPresent_ReturnedWithoutRetry()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                    await WriteAsync(stream, Rejection);
                });

                using var client = CreateClient(new StreamingOptions { ExpectContinueTimeoutMs = 30000 });

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(Body)
                    .WithHeader("Expect", "100-continue, x-custom")
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void Expect417_AfterBodySentOnWaitTimeout_ReturnedWithoutRetry()
        {
            AssertAsync.Run(async () =>
            {
                string receivedBody = null;
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);

                    // No 100: the client sends the body when its wait times out; the 417 then
                    // answers the complete request and is not repeated.
                    receivedBody = Encoding.ASCII.GetString(await ReadUpToAsync(stream, Body.Length));
                    await WriteAsync(stream, Rejection);
                });

                using var client = CreateClient(new StreamingOptions { ExpectContinueTimeoutMs = 50 });

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(Body)
                    .WithExpectContinue()
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                Assert.AreEqual(Body, receivedBody);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [TestCase("100-continue", true)]
        [TestCase("100-Continue", true)]
        [TestCase(" 100-continue ,", true)]
        [TestCase("100-continue, x-custom", false)]
        [TestCase("x-custom", false)]
        [TestCase("", false)]
        public void HasOnlyExpectContinueExpectation_ClassifiesExpectMembers(string value, bool expected)
        {
            var headers = new HttpHeaders();
            headers.Set("Expect", value);
            Assert.AreEqual(expected, ExpectContinueHelper.HasOnlyExpectContinueExpectation(headers));
        }

        private static UHttpClient CreateClient(StreamingOptions streamingOptions)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = new RawSocketTransport(
                    pool: null,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: streamingOptions),
                DisposeTransport = true
            });
        }

        private static async Task<T> WithGuard<T>(Task<T> task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            if (winner != task)
                Assert.Fail("Timed out waiting for the server observation.");
            return await task;
        }

        private static string FirstLine(string head)
        {
            int end = head.IndexOf("\r\n", StringComparison.Ordinal);
            return end < 0 ? head : head.Substring(0, end);
        }

        /// <summary>Reads one request head byte by byte.</summary>
        private static async Task<string> ReadHeadAsync(Stream stream)
        {
            var bytes = new List<byte>(256);
            var one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, 0, 1);
                if (read == 0)
                    throw new IOException("Connection closed before a request head.");

                bytes.Add(one[0]);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes.ToArray());
                if (n > 16 * 1024)
                    throw new IOException("Request head too large.");
            }
        }

        /// <summary>Reads until <paramref name="count"/> bytes arrived or the peer closed.</summary>
        private static async Task<byte[]> ReadUpToAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, total, count - total);
                }
                catch (IOException)
                {
                    break;
                }

                if (read == 0)
                    break;
                total += read;
            }

            Array.Resize(ref buffer, total);
            return buffer;
        }

        private static async Task WriteAsync(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>A read-only stream that cannot seek, so its request body is non-replayable.</summary>
        private sealed class NonSeekableStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public NonSeekableStream(byte[] data)
            {
                _data = data;
            }

            public int BytesRead => Volatile.Read(ref _position);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = Math.Min(count, _data.Length - _position);
                if (n <= 0)
                    return 0;
                Buffer.BlockCopy(_data, _position, buffer, offset, n);
                Volatile.Write(ref _position, _position + n);
                return n;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Loopback HTTP/1.1 server; one handler call per accepted connection (1-based index).</summary>
        private sealed class LoopbackServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Func<NetworkStream, int, Task> _handler;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public LoopbackServer(Func<NetworkStream, int, Task> handler)
            {
                _handler = handler;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

            private async Task AcceptLoopAsync()
            {
                while (!_disposed)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        if (_disposed)
                            return;
                        continue;
                    }

                    lock (_lock)
                        _clients.Add(client);

                    int index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleAsync(client, index));
                }
            }

            private async Task HandleAsync(TcpClient client, int index)
            {
                try
                {
                    await _handler(client.GetStream(), index);
                }
                catch (IOException)
                {
                    // The client closed the connection; the assertions observe the outcome.
                }
                catch (ObjectDisposedException)
                {
                    // Server disposed while the handler ran.
                }
                finally
                {
                    client.Close();
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                        client.Close();
                    _clients.Clear();
                }
            }
        }
    }
}
