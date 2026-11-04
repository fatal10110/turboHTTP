using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Deterministic short variant of the soak harness (#114, Tools~/soak) for HTTP/1.1 over real
    /// loopback sockets: a fixed-seed mix of buffered, uploaded, streamed and abandoned requests from
    /// concurrent workers, with and without in-flight cancellation (#293). Afterwards the parser pools hold no outstanding item and,
    /// once the transport is disposed, the server sees every connection closed (no leaked socket).
    /// The hour-long run with memory and fault profiles is Tools~/soak/run.sh.
    /// </summary>
    [TestFixture]
    public class SoakShortTests
    {
        private const int Workers = 4;
        private const int OperationsPerWorker = 30;

        [Test]
        public void Http11_MixedWorkload_NoPoolLeakAndEverySocketClosed()
        {
            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                long parsedBaseline = ParsedResponsePool.GetDiagnostics().ActiveCount;
                long scratchBaseline = HeaderParseScratchPool.GetDiagnostics().ActiveCount;

                using var server = new SoakLoopbackServer();
                var transport = new RawSocketTransport();
                var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    AllowCleartext = true,
                    DefaultTimeout = TimeSpan.FromSeconds(30),
                });

                var counts = new ConcurrentDictionary<string, int>();
                var current = new string[Workers];
                var workers = new List<Task>();
                for (int w = 0; w < Workers; w++)
                {
                    int seed = 114 * 31 + w;
                    int worker = w;
                    workers.Add(Task.Run(() => RunWorkerAsync(client, server, seed, counts, current, worker, includeCancel: false, watchdog.Token)));
                }

                await WithWatchdog(Task.WhenAll(workers), watchdog.Token,
                    () => "workers; in progress: " + string.Join(", ", current) + "; server: " + server.Describe());

                Assert.AreEqual(Workers * OperationsPerWorker, Sum(counts), "every operation finished as expected");
                Assert.Greater(Get(counts, "abandoned"), 0, "the fixed seed exercises abandoned bodies");
                Assert.LessOrEqual(ParsedResponsePool.GetDiagnostics().ActiveCount - parsedBaseline, 0,
                    "ParsedResponse instances not returned");
                Assert.LessOrEqual(HeaderParseScratchPool.GetDiagnostics().ActiveCount - scratchBaseline, 0,
                    "HeaderParseScratch instances not returned");
                Assert.Less(server.AcceptCount, Workers * OperationsPerWorker,
                    "keep-alive connections are reused");

                client.Dispose();
                transport.Dispose();

                // Each server connection ends when it reads EOF or fails a write: a socket the client
                // leaked keeps its handler waiting until the watchdog.
                await WithWatchdog(server.AllConnectionsClosedAsync(), watchdog.Token, "server connections");
                Assert.AreEqual(0, server.OpenConnections);
            });
        }

        /// <summary>
        /// The same mix plus cancellation while the server holds the response head. On Unity 2021.3
        /// Mono (Windows Editor) about one run in 25 used to never end a cancelled request even though
        /// the socket was disposed and the server saw it close (#293). The head read now races the
        /// token, so cancellation ends the request at once whatever the platform read does.
        /// </summary>
        [Test]
        public void Http11_MixedWorkloadWithInFlightCancel_EveryCancelledRequestEnds()
        {
            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var server = new SoakLoopbackServer();
                var transport = new RawSocketTransport();
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport, AllowCleartext = true });
                var counts = new ConcurrentDictionary<string, int>();
                var current = new string[Workers];
                var workers = new List<Task>();
                for (int w = 0; w < Workers; w++)
                {
                    int seed = 114 * 31 + w;
                    int worker = w;
                    workers.Add(Task.Run(() => RunWorkerAsync(client, server, seed, counts, current, worker, includeCancel: true, watchdog.Token)));
                }

                try
                {
                    await WithWatchdog(Task.WhenAll(workers), watchdog.Token,
                        () => "workers; in progress: " + string.Join(", ", current) + "; server: " + server.Describe());
                    Assert.Greater(Get(counts, "cancelled"), 0, "the fixed seed exercises cancellation");
                }
                finally
                {
                    client.Dispose();
                    transport.Dispose();
                }
            });
        }

        [Test]
        public void Http11_AbandonedChunkedBodies_FaultedTrailersAreObserved()
        {
            AssertAsync.Run(async () =>
            {
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var unobserved = new ConcurrentQueue<Exception>();
                EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
                {
                    foreach (var inner in e.Exception.InnerExceptions)
                    {
                        if (inner is ObjectDisposedException disposed && disposed.ObjectName == "Http11ResponseBodySource")
                            unobserved.Enqueue(inner);
                    }
                };

                using var server = new SoakLoopbackServer();
                var transport = new RawSocketTransport();
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport, AllowCleartext = true });
                TaskScheduler.UnobservedTaskException += handler;
                try
                {
                    // The soak harness saw these on HTTP/1.1 rows: a chunked body's trailers task faulted
                    // on dispose (or on a read error), nobody awaited it, and its finalizer reported it.
                    await AbandonChunkedBodiesAsync(client, server, 8, watchdog.Token);
                    for (int i = 0; i < 3; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
                finally
                {
                    TaskScheduler.UnobservedTaskException -= handler;
                    client.Dispose();
                    transport.Dispose();
                }

                Assert.AreEqual(0, unobserved.Count,
                    "a disposed chunked body left a faulted, unobserved trailers task behind");
            });
        }

        // Separate, non-inlined: no local of the caller keeps a body source (or its trailers task) reachable.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async Task AbandonChunkedBodiesAsync(UHttpClient client, SoakLoopbackServer server, int count, CancellationToken ct)
        {
            for (int i = 0; i < count; i++)
            {
                using var response = await client.SendStreamingAsync(client.Get(server.Url("/chunked/" + (96 * 1024))), ct);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var head = new byte[64];
                Assert.Greater(await response.Body.ReadAsync(head, 0, head.Length, ct), 0);
            }
        }

        private static async Task RunWorkerAsync(
            UHttpClient client, SoakLoopbackServer server, int seed,
            ConcurrentDictionary<string, int> counts, string[] current, int worker, bool includeCancel,
            CancellationToken watchdog)
        {
            var random = new Random(seed);
            for (int i = 0; i < OperationsPerWorker; i++)
            {
                int op = random.Next(includeCancel ? 5 : 4);
                int size = 1 + random.Next(48 * 1024);
                Volatile.Write(ref current[worker], "w" + worker + " op " + i + " kind " + op + " size " + size);
                switch (op)
                {
                    case 0:
                        using (var response = await client.SendBufferedAsync(
                                   client.Get(server.Url("/bytes/" + size)), watchdog).ConfigureAwait(false))
                        {
                            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                            AssertPattern(response.Body.ToArray(), size);
                        }

                        Increment(counts, "buffered");
                        break;
                    case 1:
                        {
                            var body = new byte[size];
                            random.NextBytes(body);
                            using (var response = await client.SendBufferedAsync(
                                       client.Post(server.Url("/echo")).WithBody(body), watchdog).ConfigureAwait(false))
                            {
                                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                                CollectionAssert.AreEqual(body, response.Body.ToArray());
                            }

                            Increment(counts, "upload");
                            break;
                        }
                    case 2:
                        using (var response = await client.SendStreamingAsync(
                                   client.Get(server.Url("/chunked/" + size)), watchdog).ConfigureAwait(false))
                        {
                            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                            AssertPattern(await ReadToEndAsync(response.Body, watchdog).ConfigureAwait(false), size);
                        }

                        Increment(counts, "streamed");
                        break;
                    case 3:
                        // Abandoned mid-body: the unread rest means the connection cannot be reused.
                        using (var response = await client.SendStreamingAsync(
                                   client.Get(server.Url("/bytes/" + (256 * 1024))), watchdog).ConfigureAwait(false))
                        {
                            var head = new byte[512];
                            int n = await response.Body.ReadAsync(head, 0, head.Length, watchdog).ConfigureAwait(false);
                            Assert.Greater(n, 0);
                        }

                        Increment(counts, "abandoned");
                        break;
                    default:
                        await RunCancelledAsync(client, server, phase => Volatile.Write(ref current[worker], "w" + worker + " op " + i + " cancel: " + phase), watchdog).ConfigureAwait(false);
                        Increment(counts, "cancelled");
                        break;
                }
            }
        }

        private static async Task RunCancelledAsync(
            UHttpClient client, SoakLoopbackServer server, Action<string> phase, CancellationToken watchdog)
        {
            string id = Guid.NewGuid().ToString("N");
            var held = server.ExpectHold(id);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(watchdog);
            var send = client.SendBufferedAsync(client.Get(server.Url("/hold/" + id)), cancel.Token);

            // Cancel only once the server has the request, so the in-flight path is the one tested.
            phase("waiting for the server to hold " + id);
            await WithWatchdog(held, watchdog, "hold " + id).ConfigureAwait(false);
            phase("cancelling");
            cancel.Cancel();
            phase("cancelled (Cancel returned), waiting for the request to end");
            try
            {
                using var response = await send.ConfigureAwait(false);
                Assert.Fail("a held request must not complete, got " + response.StatusCode);
            }
            catch (OperationCanceledException) when (!watchdog.IsCancellationRequested)
            {
            }
            catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Cancelled)
            {
            }
        }

        private static async Task<byte[]> ReadToEndAsync(Stream body, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = await body.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
                buffer.Write(chunk, 0, n);
            return buffer.ToArray();
        }

        private static void AssertPattern(byte[] body, int size)
        {
            Assert.AreEqual(size, body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] != SoakLoopbackServer.PatternByte(i))
                    Assert.Fail("body byte " + i + " corrupted");
            }
        }

        private static Task WithWatchdog(Task task, CancellationToken watchdog, string what) =>
            WithWatchdog(task, watchdog, () => what);

        private static async Task WithWatchdog(Task task, CancellationToken watchdog, Func<string> what)
        {
            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (watchdog.Register(() => fired.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, fired.Task).ConfigureAwait(false) != task)
                    Assert.Fail("watchdog fired waiting for " + what());
            }

            await task.ConfigureAwait(false);
        }

        private static void Increment(ConcurrentDictionary<string, int> counts, string key) =>
            counts.AddOrUpdate(key, 1, (_, v) => v + 1);

        private static int Get(ConcurrentDictionary<string, int> counts, string key) =>
            counts.TryGetValue(key, out var v) ? v : 0;

        private static int Sum(ConcurrentDictionary<string, int> counts)
        {
            int total = 0;
            foreach (var kvp in counts)
                total += kvp.Value;
            return total;
        }

        /// <summary>
        /// Keep-alive HTTP/1.1 loopback server: /bytes/N (Content-Length), /chunked/N, /echo, and
        /// /hold/ID, which answers nothing and waits for the client to close the connection.
        /// </summary>
        private sealed class SoakLoopbackServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly ConcurrentDictionary<int, Task> _connections = new ConcurrentDictionary<int, Task>();
            private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _holds =
                new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
            private readonly ConcurrentDictionary<string, int> _holdHits = new ConcurrentDictionary<string, int>();
            private readonly Task _acceptLoop;
            private int _acceptCount;
            private int _open;

            public SoakLoopbackServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _acceptLoop = AcceptLoopAsync();
            }

            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public int OpenConnections => Volatile.Read(ref _open);

            public static byte PatternByte(int index) => (byte)((index * 31 + 7) & 0xFF);

            public string Url(string path) =>
                "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + path;

            public Task ExpectHold(string id)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _holds[id] = tcs;
                return tcs.Task;
            }

            public Task AllConnectionsClosedAsync() => Task.WhenAll(_connections.Values);

            /// <summary>Watchdog diagnostics: accepted/open connections and every /hold id requested more than once.</summary>
            public string Describe()
            {
                var repeated = new List<string>();
                foreach (var kvp in _holdHits)
                {
                    if (kvp.Value > 1)
                        repeated.Add(kvp.Key + " x" + kvp.Value);
                }

                return "accepted " + AcceptCount + ", open " + OpenConnections + ", repeated holds [" + string.Join(", ", repeated) + "]";
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    int index = Interlocked.Increment(ref _acceptCount);
                    Interlocked.Increment(ref _open);
                    _connections[index] = Task.Run(() => ServeAsync(tcp));
                }
            }

            private async Task ServeAsync(TcpClient tcp)
            {
                try
                {
                    using (tcp)
                    using (var stream = tcp.GetStream())
                    {
                        var reader = new HeadReader(stream);
                        while (true)
                        {
                            var head = await reader.ReadHeadAsync(_cts.Token).ConfigureAwait(false);
                            if (head == null)
                                return; // EOF: the client closed the connection.

                            string[] requestLine = head[0].Split(' ');
                            string path = requestLine[1];
                            long contentLength = 0;
                            for (int i = 1; i < head.Count; i++)
                            {
                                if (head[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    contentLength = long.Parse(head[i].Substring(15).Trim());
                            }

                            var body = await reader.ReadBodyAsync((int)contentLength, _cts.Token).ConfigureAwait(false);
                            if (body == null)
                                return;

                            if (path.StartsWith("/hold/", StringComparison.Ordinal))
                            {
                                _holdHits.AddOrUpdate(path.Substring(6), 1, (_, v) => v + 1);
                                if (_holds.TryRemove(path.Substring(6), out var held))
                                    held.TrySetResult(true);
                                // Never answer: only the client closing the connection ends this.
                                var sink = new byte[1];
                                while (await stream.ReadAsync(sink, 0, 1, _cts.Token).ConfigureAwait(false) > 0)
                                {
                                }

                                return;
                            }

                            await WriteResponseAsync(stream, path, body).ConfigureAwait(false);
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    Interlocked.Decrement(ref _open);
                }
            }

            private async Task WriteResponseAsync(NetworkStream stream, string path, byte[] requestBody)
            {
                if (path == "/echo")
                {
                    await WriteHeadAsync(stream, "Content-Length: " + requestBody.Length).ConfigureAwait(false);
                    await stream.WriteAsync(requestBody, 0, requestBody.Length, _cts.Token).ConfigureAwait(false);
                    return;
                }

                bool chunked = path.StartsWith("/chunked/", StringComparison.Ordinal);
                int size = int.Parse(path.Substring(path.LastIndexOf('/') + 1));
                var payload = new byte[size];
                for (int i = 0; i < size; i++)
                    payload[i] = PatternByte(i);

                if (!chunked)
                {
                    await WriteHeadAsync(stream, "Content-Length: " + size).ConfigureAwait(false);
                    await stream.WriteAsync(payload, 0, size, _cts.Token).ConfigureAwait(false);
                    return;
                }

                await WriteHeadAsync(stream, "Transfer-Encoding: chunked").ConfigureAwait(false);
                int offset = 0;
                int piece = Math.Max(1, size / 3);
                while (offset < size)
                {
                    int n = Math.Min(piece, size - offset);
                    var sizeLine = Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n");
                    await stream.WriteAsync(sizeLine, 0, sizeLine.Length, _cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, offset, n, _cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(Crlf, 0, 2, _cts.Token).ConfigureAwait(false);
                    offset += n;
                }

                var last = Encoding.ASCII.GetBytes("0\r\n\r\n");
                await stream.WriteAsync(last, 0, last.Length, _cts.Token).ConfigureAwait(false);
            }

            private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

            private Task WriteHeadAsync(NetworkStream stream, string framing)
            {
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + framing + "\r\nConnection: keep-alive\r\n\r\n");
                return stream.WriteAsync(head, 0, head.Length, _cts.Token);
            }
        }

        /// <summary>Buffered reader of request heads and Content-Length bodies over one connection.</summary>
        private sealed class HeadReader
        {
            private const int MaxHeadBytes = 16 * 1024;
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[64 * 1024];
            private int _start;
            private int _end;

            public HeadReader(Stream stream)
            {
                _stream = stream;
            }

            /// <summary>The head lines without the terminating blank line, or null at EOF.</summary>
            public async Task<List<string>> ReadHeadAsync(CancellationToken ct)
            {
                while (true)
                {
                    int terminator = IndexOfHeadEnd();
                    if (terminator >= 0)
                    {
                        string text = Encoding.ASCII.GetString(_buffer, _start, terminator - _start);
                        _start = terminator + 4;
                        return new List<string>(text.Split(new[] { "\r\n" }, StringSplitOptions.None));
                    }

                    if (_end - _start > MaxHeadBytes)
                        throw new IOException("request head too large");
                    if (!await FillAsync(ct).ConfigureAwait(false))
                        return null;
                }
            }

            public async Task<byte[]> ReadBodyAsync(int length, CancellationToken ct)
            {
                var body = new byte[length];
                int copied = 0;
                while (copied < length)
                {
                    if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                        return null;
                    int n = Math.Min(length - copied, _end - _start);
                    Buffer.BlockCopy(_buffer, _start, body, copied, n);
                    _start += n;
                    copied += n;
                }

                return body;
            }

            private int IndexOfHeadEnd()
            {
                for (int i = _start; i + 3 < _end; i++)
                {
                    if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                        return i;
                }

                return -1;
            }

            private async Task<bool> FillAsync(CancellationToken ct)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }

                int n = await _stream.ReadAsync(_buffer, _end, _buffer.Length - _end, ct).ConfigureAwait(false);
                if (n == 0)
                    return false;
                _end += n;
                return true;
            }
        }
    }
}
