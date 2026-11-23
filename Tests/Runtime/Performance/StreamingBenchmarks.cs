using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Phase 22a.6 Step 2 (#102) loopback benchmarks inside the Unity runtime (Editor Mono, Mono and IL2CPP
    /// players): HTTP/1.1 round trips through <see cref="RawSocketTransport"/> against an in-process
    /// <see cref="TcpListener"/> peer, buffered against streaming, and the streaming download against a raw-socket
    /// client reading the same bytes.
    /// <para>
    /// <b>Budgets.</b> The plan's guardrails are "&lt;= 5 % latency regression for buffered small-body paths vs the
    /// 22.3 baseline" and "streaming throughput within 5-10 % of raw". No numeric 22.3 baseline survives
    /// (<c>Tools~/bench/README.md</c>, "The committed baseline"), and a baseline recorded on one machine does not
    /// transfer to another, so this fixture checks both guardrails against references measured in the same run:
    /// </para>
    /// <list type="bullet">
    /// <item>Buffered small body: since 22a a buffered response is the streaming response collected by the
    /// dispatch bridge, so the cost 22a added to the 22.3 buffered path is the bridge over a streaming read of the
    /// same body into a caller buffer. Budget: buffered p50 &lt;= streaming p50 x 1.05
    /// (<see cref="SmallBodyLatencyBudgetPercent"/>) for 1 KiB and 32 KiB GETs.</item>
    /// <item>Streaming throughput: 1 MiB streaming download MiB/s &gt;= raw-socket MiB/s x 0.90
    /// (<see cref="RawThroughputBudgetPercent"/>, the upper end of the plan's 5-10 %).</item>
    /// </list>
    /// <para>
    /// Loopback wall-clock time on a shared host is not repeatable to 5 % (the CoreCLR harness saw -43 % to +8 %
    /// between identical runs), so those budgets are printed as advisory and fail the test only with
    /// <c>TURBOHTTP_BENCH_GATE_LATENCY=1</c> on a quiet machine. Every run fails on a gross regression
    /// (<see cref="GrossLatencyFactor"/>, <see cref="GrossThroughputFactor"/>), on a wrong byte count, and when a
    /// scenario needs more than one connection (connection reuse is what the latency rows measure). Per-chunk
    /// allocation is gated in <see cref="StreamingAllocationGateTests"/>; the CoreCLR harness
    /// (<c>Tools~/bench</c>) adds HTTP/2, TLS, allocation per request against <c>baseline.json</c>, and the 100 MiB
    /// memory rows.
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("Benchmark")]
    public sealed class StreamingBenchmarks
    {
        internal const string GateLatencyEnvironmentVariable = "TURBOHTTP_BENCH_GATE_LATENCY";
        internal const int SmallBodyLatencyBudgetPercent = 5;
        internal const int RawThroughputBudgetPercent = 10;
        internal const double GrossLatencyFactor = 3.0;
        internal const double GrossThroughputFactor = 0.25;

        private const int Rounds = 5;
        private const int SmallIterations = 100;
        private const int LargeIterations = 8;
        private const int UploadIterations = 4;
        private const int ReadBufferBytes = 16 * 1024;
        private const int OneKiB = 1024;
        private const int ThirtyTwoKiB = 32 * 1024;
        private const int OneMiB = 1024 * 1024;
        private const int FiveMiB = 5 * 1024 * 1024;

        private static readonly TimeSpan Bound = TimeSpan.FromMinutes(4);

        [Test]
        public void Http11SmallBodies_BufferedAgainstStreaming_WithinLatencyBudget()
        {
            var report = new BenchReport();
            AssertAsync.Run(async () =>
            {
                using var server = new BenchServer();
                using var client = CreateClient();

                foreach (var size in new[] { OneKiB, ThirtyTwoKiB })
                {
                    var url = server.Url("/bytes/" + size.ToString(CultureInfo.InvariantCulture));
                    var buffered = new LatencySamples("h1-get-" + Label(size) + "-buffered", size);
                    var streaming = new LatencySamples("h1-get-" + Label(size) + "-streaming", size);
                    var sink = new byte[size];

                    await GetBufferedAsync(client, url, size);
                    await GetStreamingAsync(client, url, sink, size);

                    // Alternating order per round, so drift on the host weighs on both rows alike.
                    for (var round = 0; round < Rounds; round++)
                    {
                        if (round % 2 == 0)
                        {
                            await MeasureRoundAsync(buffered, SmallIterations, () => GetBufferedAsync(client, url, size));
                            await MeasureRoundAsync(streaming, SmallIterations, () => GetStreamingAsync(client, url, sink, size));
                        }
                        else
                        {
                            await MeasureRoundAsync(streaming, SmallIterations, () => GetStreamingAsync(client, url, sink, size));
                            await MeasureRoundAsync(buffered, SmallIterations, () => GetBufferedAsync(client, url, size));
                        }
                    }

                    report.Add(buffered);
                    report.Add(streaming);
                    report.CheckLatency(
                        "buffered " + Label(size) + " GET vs streaming read of the same body",
                        buffered,
                        streaming,
                        SmallBodyLatencyBudgetPercent);
                }

                var post = new LatencySamples("h1-post-1k-buffered", OneKiB);
                var postBody = Pattern(OneKiB);
                var postUrl = server.Url("/echo");
                await PostBufferedAsync(client, postUrl, postBody);
                for (var round = 0; round < Rounds; round++)
                    await MeasureRoundAsync(post, SmallIterations, () => PostBufferedAsync(client, postUrl, postBody));
                report.Add(post);

                Assert.AreEqual(1, server.AcceptCount, "every request of the run must reuse one kept-alive connection");
            }, Bound);

            report.Finish();
        }

        [Test]
        public void Http11OneMiBDownload_StreamingAgainstRawSocket_WithinThroughputBudget()
        {
            var report = new BenchReport();
            AssertAsync.Run(async () =>
            {
                using var server = new BenchServer();
                using var client = CreateClient();
                using var raw = new RawHttp11Client(server.Port);

                var path = "/bytes/" + OneMiB.ToString(CultureInfo.InvariantCulture);
                var url = server.Url(path);
                var streaming = new LatencySamples("h1-get-1m-streaming", OneMiB);
                var buffered = new LatencySamples("h1-get-1m-buffered", OneMiB);
                var rawRow = new LatencySamples("raw-h1-get-1m", OneMiB);
                var readBuffer = new byte[ReadBufferBytes];

                await GetStreamingAsync(client, url, readBuffer, OneMiB);
                await GetBufferedAsync(client, url, OneMiB);
                await raw.GetAsync(path, readBuffer, OneMiB);

                for (var round = 0; round < Rounds; round++)
                {
                    await MeasureRoundAsync(rawRow, LargeIterations, () => raw.GetAsync(path, readBuffer, OneMiB));
                    await MeasureRoundAsync(streaming, LargeIterations, () => GetStreamingAsync(client, url, readBuffer, OneMiB));
                    await MeasureRoundAsync(buffered, LargeIterations, () => GetBufferedAsync(client, url, OneMiB));
                }

                report.Add(rawRow);
                report.Add(streaming);
                report.Add(buffered);
                report.CheckThroughput("streaming 1 MiB GET vs raw socket", streaming, rawRow, RawThroughputBudgetPercent);

                // The raw client holds one connection, the TurboHTTP client another.
                Assert.AreEqual(2, server.AcceptCount, "every request of the run must reuse its kept-alive connection");
            }, Bound);

            report.Finish();
        }

        // Plan item 3: a 5 MiB upload from a byte[] against the same bytes from a stream body. Reported, not
        // budgeted (the plan names no guardrail for it); the server checks every byte arrived.
        [Test]
        public void Http11FiveMiBUpload_BufferedAgainstStreamBody_DeliversEveryByte()
        {
            var report = new BenchReport();
            AssertAsync.Run(async () =>
            {
                using var server = new BenchServer();
                using var client = CreateClient();

                var payload = Pattern(FiveMiB);
                var url = server.Url("/sink");
                var buffered = new LatencySamples("h1-post-5m-buffered", FiveMiB);
                var stream = new LatencySamples("h1-post-5m-stream", FiveMiB);

                await UploadAsync(client, url, payload, asStream: false, server);
                await UploadAsync(client, url, payload, asStream: true, server);
                for (var round = 0; round < Rounds; round++)
                {
                    await MeasureRoundAsync(buffered, UploadIterations, () => UploadAsync(client, url, payload, false, server));
                    await MeasureRoundAsync(stream, UploadIterations, () => UploadAsync(client, url, payload, true, server));
                }

                report.Add(buffered);
                report.Add(stream);
                Assert.AreEqual(1, server.AcceptCount, "every upload of the run must reuse one kept-alive connection");
            }, Bound);

            report.Finish();
        }

        private static UHttpClient CreateClient()
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = new RawSocketTransport(),
                DisposeTransport = true
            });
        }

        private static async Task MeasureRoundAsync(LatencySamples samples, int iterations, Func<Task> operation)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var round = new long[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await operation().ConfigureAwait(false);
                round[i] = Stopwatch.GetTimestamp() - start;
            }

            samples.AddRound(round);
        }

        private static async Task GetBufferedAsync(UHttpClient client, string url, int expectedBytes)
        {
            using var response = await client.Get(url).SendBufferedAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(expectedBytes, response.Body.Length, "buffered body length");
        }

        private static async Task GetStreamingAsync(UHttpClient client, string url, byte[] buffer, int expectedBytes)
        {
            var response = await client.Get(url).SendStreamingAsync().ConfigureAwait(false);
            try
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                long total = 0;
                var offset = 0;
                while (true)
                {
                    // Small bodies land in one caller array (the buffered row's byte[]); large ones reuse the buffer.
                    if (offset == buffer.Length)
                        offset = 0;
                    var read = await response.Body.ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    offset += read;
                    total += read;
                }

                Assert.AreEqual(expectedBytes, total, "streamed body length");
            }
            finally
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task PostBufferedAsync(UHttpClient client, string url, byte[] body)
        {
            using var response = await client.Post(url).WithBody(body).SendBufferedAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(body.Length, response.Body.Length, "echoed body length");
        }

        private static async Task UploadAsync(UHttpClient client, string url, byte[] payload, bool asStream, BenchServer server)
        {
            var request = client.Post(url);
            if (asStream)
                request.WithStreamBody(new MemoryStream(payload, writable: false), payload.Length);
            else
                request.WithBody(payload);

            using var response = await request.SendBufferedAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(payload.Length, server.LastSinkBytes, "the server must receive the whole upload");
        }

        private static string Label(int bytes)
        {
            return bytes >= OneMiB
                ? (bytes / OneMiB).ToString(CultureInfo.InvariantCulture) + "m"
                : (bytes / 1024).ToString(CultureInfo.InvariantCulture) + "k";
        }

        private static byte[] Pattern(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((i * 31) % 251);
            return bytes;
        }

        /// <summary>Per-request ticks of one scenario, by round.</summary>
        private sealed class LatencySamples
        {
            private readonly List<long[]> _rounds = new List<long[]>();

            internal LatencySamples(string name, int bodyBytes)
            {
                Name = name;
                BodyBytes = bodyBytes;
            }

            internal string Name { get; }
            internal int BodyBytes { get; }

            internal void AddRound(long[] ticks) => _rounds.Add(ticks);

            /// <summary>The lowest per-round median: host load only adds time, so the best round repeats best.</summary>
            internal double P50Microseconds
            {
                get
                {
                    var best = double.MaxValue;
                    foreach (var round in _rounds)
                        best = Math.Min(best, Percentile(round, 0.50));
                    return best;
                }
            }

            internal double P95Microseconds => Percentile(All(), 0.95);

            internal double MeanMicroseconds
            {
                get
                {
                    var all = All();
                    double sum = 0;
                    foreach (var value in all)
                        sum += value;
                    return ToMicroseconds(sum / all.Length);
                }
            }

            internal double MebibytesPerSecond => BodyBytes / (P50Microseconds / 1_000_000.0) / (1024.0 * 1024.0);

            internal int Requests => All().Length;

            private long[] All()
            {
                var count = 0;
                foreach (var round in _rounds)
                    count += round.Length;
                var all = new long[count];
                var offset = 0;
                foreach (var round in _rounds)
                {
                    Array.Copy(round, 0, all, offset, round.Length);
                    offset += round.Length;
                }

                return all;
            }

            private static double Percentile(long[] ticks, double quantile)
            {
                var sorted = (long[])ticks.Clone();
                Array.Sort(sorted);
                var index = (int)Math.Ceiling(quantile * sorted.Length) - 1;
                return ToMicroseconds(sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))]);
            }

            private static double ToMicroseconds(double ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
        }

        /// <summary>Prints the rows and the budget verdicts; fails on gross regressions, and on budgets when gated.</summary>
        private sealed class BenchReport
        {
            private readonly StringBuilder _failures = new StringBuilder();
            private readonly bool _gated = string.Equals(
                Environment.GetEnvironmentVariable(GateLatencyEnvironmentVariable), "1", StringComparison.Ordinal);

            internal void Add(LatencySamples samples)
            {
                TestContext.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "[Phase22a.6][Bench] {0} runtime={1} requests={2} p50_us={3:F1} p95_us={4:F1} mean_us={5:F1}{6}",
                    samples.Name,
                    RuntimeLabel(),
                    samples.Requests,
                    samples.P50Microseconds,
                    samples.P95Microseconds,
                    samples.MeanMicroseconds,
                    samples.BodyBytes >= OneMiB
                        ? string.Format(CultureInfo.InvariantCulture, " mib_s={0:F1}", samples.MebibytesPerSecond)
                        : string.Empty));
            }

            internal void CheckLatency(string what, LatencySamples measured, LatencySamples reference, int budgetPercent)
            {
                var ratio = measured.P50Microseconds / reference.P50Microseconds;
                Judge(what, ratio, 1.0 + budgetPercent / 100.0, GrossLatencyFactor, higherIsWorse: true);
            }

            internal void CheckThroughput(string what, LatencySamples measured, LatencySamples reference, int budgetPercent)
            {
                var ratio = measured.MebibytesPerSecond / reference.MebibytesPerSecond;
                Judge(what, ratio, 1.0 - budgetPercent / 100.0, GrossThroughputFactor, higherIsWorse: false);
            }

            internal void Finish()
            {
                if (_failures.Length > 0)
                    Assert.Fail(_failures.ToString());
            }

            private void Judge(string what, double ratio, double budget, double gross, bool higherIsWorse)
            {
                var withinBudget = higherIsWorse ? ratio <= budget : ratio >= budget;
                var withinGross = higherIsWorse ? ratio <= gross : ratio >= gross;
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "[Phase22a.6][Budget] {0}: ratio={1:F3} budget={2}{3:F2} gross={2}{4:F2} verdict={5} ({6})",
                    what,
                    ratio,
                    higherIsWorse ? "<=" : ">=",
                    budget,
                    gross,
                    withinBudget ? "within" : "over",
                    _gated ? "gated" : "advisory; " + GateLatencyEnvironmentVariable + "=1 gates it");
                TestContext.WriteLine(line);

                if (!withinGross || (_gated && !withinBudget))
                    _failures.AppendLine(line);
            }

            private static string RuntimeLabel()
            {
#if ENABLE_IL2CPP
                const string backend = "il2cpp";
#else
                const string backend = "mono";
#endif
                return backend + (UnityEngine.Application.isEditor ? "-editor" : "-player");
            }
        }

        /// <summary>
        /// HTTP/1.1 keep-alive peer on loopback, one thread per connection: <c>GET /bytes/N</c> (N pattern bytes),
        /// <c>POST /echo</c> (the body back) and <c>POST /sink</c> (counts the body, answers empty).
        /// </summary>
        private sealed class BenchServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _acceptThread;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly Dictionary<string, byte[]> _responses = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private int _acceptCount;
            private long _lastSinkBytes;
            private volatile bool _disposed;

            internal BenchServer()
            {
                foreach (var size in new[] { OneKiB, ThirtyTwoKiB, OneMiB })
                    _responses["/bytes/" + size.ToString(CultureInfo.InvariantCulture)] = BuildResponse(Pattern(size), size);

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "StreamingBenchmarks.Accept" };
                _acceptThread.Start();
            }

            internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            internal int AcceptCount => Volatile.Read(ref _acceptCount);
            internal long LastSinkBytes => Interlocked.Read(ref _lastSinkBytes);

            internal string Url(string path) => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + path;

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_clients)
                {
                    foreach (var client in _clients)
                        client.Close();
                    _clients.Clear();
                }
            }

            private void AcceptLoop()
            {
                while (!_disposed)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    client.NoDelay = true;
                    lock (_clients)
                        _clients.Add(client);
                    Interlocked.Increment(ref _acceptCount);
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "StreamingBenchmarks.Serve" }.Start();
                }
            }

            private void Serve(TcpClient client)
            {
                var buffer = new byte[64 * 1024];
                var buffered = 0;
                try
                {
                    var stream = client.GetStream();
                    while (!_disposed)
                    {
                        int headEnd;
                        while ((headEnd = IndexOfHeadEnd(buffer, buffered)) < 0)
                        {
                            if (buffered == buffer.Length)
                                throw new InvalidDataException("request head over 64 KiB");
                            var read = stream.Read(buffer, buffered, buffer.Length - buffered);
                            if (read == 0)
                                return;
                            buffered += read;
                        }

                        var head = Encoding.ASCII.GetString(buffer, 0, headEnd);
                        var bodyLength = ContentLength(head);
                        var consumed = headEnd + 4;
                        var path = RequestPath(head);

                        if (path == "/echo")
                        {
                            var body = new byte[bodyLength];
                            var copied = Math.Min(bodyLength, buffered - consumed);
                            Buffer.BlockCopy(buffer, consumed, body, 0, copied);
                            consumed += copied;
                            ReadExactly(stream, body, copied, bodyLength - copied);
                            Compact(buffer, ref buffered, consumed);
                            var response = BuildResponse(body, body.Length);
                            stream.Write(response, 0, response.Length);
                            continue;
                        }

                        // Discard the body (a GET has none).
                        long remaining = bodyLength;
                        var inBuffer = (int)Math.Min(remaining, buffered - consumed);
                        consumed += inBuffer;
                        remaining -= inBuffer;
                        Compact(buffer, ref buffered, consumed);
                        while (remaining > 0)
                        {
                            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0)
                                return;
                            remaining -= read;
                        }

                        if (path == "/sink")
                        {
                            Interlocked.Exchange(ref _lastSinkBytes, bodyLength);
                            var empty = BuildResponse(Array.Empty<byte>(), 0);
                            stream.Write(empty, 0, empty.Length);
                            continue;
                        }

                        if (!_responses.TryGetValue(path, out var canned))
                            canned = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                        stream.Write(canned, 0, canned.Length);
                    }
                }
                catch (IOException)
                {
                    // The client closed the connection, or the server is disposing.
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
                finally
                {
                    client.Close();
                }
            }

            private static byte[] BuildResponse(byte[] body, int length)
            {
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
                    length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                var response = new byte[head.Length + length];
                Buffer.BlockCopy(head, 0, response, 0, head.Length);
                Buffer.BlockCopy(body, 0, response, head.Length, length);
                return response;
            }

            private static void ReadExactly(NetworkStream stream, byte[] destination, int offset, int count)
            {
                while (count > 0)
                {
                    var read = stream.Read(destination, offset, count);
                    if (read == 0)
                        throw new IOException("connection closed inside the request body");
                    offset += read;
                    count -= read;
                }
            }

            private static void Compact(byte[] buffer, ref int buffered, int consumed)
            {
                Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered - consumed);
                buffered -= consumed;
            }

            private static string RequestPath(string head)
            {
                var firstSpace = head.IndexOf(' ');
                var secondSpace = head.IndexOf(' ', firstSpace + 1);
                return head.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
            }

            private static int ContentLength(string head)
            {
                foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        return int.Parse(line.Substring("Content-Length:".Length).Trim(), CultureInfo.InvariantCulture);
                }

                return 0;
            }
        }

        /// <summary>
        /// The raw-socket floor: one kept-alive connection, a fixed GET, the response head skipped and the
        /// Content-Length body read into the caller's buffer. No parsing beyond what the fixed peer needs.
        /// </summary>
        private sealed class RawHttp11Client : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly byte[] _head = new byte[4096];

            internal RawHttp11Client(int port)
            {
                _client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                _client.Connect(IPAddress.Loopback, port);
                _stream = _client.GetStream();
            }

            internal async Task GetAsync(string path, byte[] buffer, int expectedBytes)
            {
                var request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                await _stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);

                // One response is in flight, so whatever follows the head in the head buffer is body.
                var headBytes = 0;
                int headEnd;
                while ((headEnd = IndexOfHeadEnd(_head, headBytes)) < 0)
                {
                    if (headBytes == _head.Length)
                        throw new InvalidDataException("response head over 4 KiB");
                    var read = await _stream.ReadAsync(_head, headBytes, _head.Length - headBytes).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("connection closed inside the response head");
                    headBytes += read;
                }

                var remaining = expectedBytes - (headBytes - (headEnd + 4));
                while (remaining > 0)
                {
                    var read = await _stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("connection closed inside the response body");
                    remaining -= read;
                }
            }

            public void Dispose()
            {
                _client.Close();
            }
        }

        private static int IndexOfHeadEnd(byte[] buffer, int count)
        {
            for (var i = 3; i < count; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                    return i - 3;
            }

            return -1;
        }
    }
}
