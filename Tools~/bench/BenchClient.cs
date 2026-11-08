using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tools.Bench
{
    internal sealed class BenchOptions
    {
        public string PkiDir;
        public string OutDir;
        public int Iterations = 2000;
        public int Warmup = 300;
        public int Rounds = 5;
        public bool Heavy;
        public string[] Only;
        public string ComparePath;
        public string WriteBaselinePath;
        public double BudgetPercent = 5;
    }

    /// <summary>
    /// The `run` process: starts `serve`, drives each scenario sequentially (one request in flight, so latency
    /// is the client's round trip and not queueing) and reports per-request latency, managed bytes allocated
    /// per request (process-wide <see cref="GC.GetTotalAllocatedBytes(bool)"/>; the server is another process)
    /// and throughput or peak heap where the scenario is about those.
    /// </summary>
    internal sealed class BenchClient
    {
        private const int ReadBufferBytes = 16 * 1024;

        private readonly BenchOptions _options;
        private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
        private Process _server;
        private UHttpClient _client;
        private string _h1;
        private string _h2;

        public BenchClient(BenchOptions options)
        {
            _options = options;
        }

        public async Task<int> RunAsync()
        {
            Directory.CreateDirectory(_options.OutDir);
            var (h1Port, tlsPort) = await StartServerAsync().ConfigureAwait(false);
            _h1 = "http://127.0.0.1:" + h1Port.ToString(CultureInfo.InvariantCulture);
            _h2 = "https://127.0.0.1:" + tlsPort.ToString(CultureInfo.InvariantCulture);
            try
            {
                _client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true,
                    AllowCleartext = true, // the loopback HTTP/1.1 rows; HTTP/2 rows verify the per-run root
                    DefaultTimeout = TimeSpan.FromSeconds(120),
                    FollowRedirects = false,
                    MaxBufferedResponseBodySize = 128L * 1024 * 1024, // the 100 MB buffered row
                });

                var results = new List<ScenarioResult>();
                using (var raw = new RawHttp11Client(IPAddress.Loopback, h1Port))
                {
                    foreach (var scenario in Scenarios(raw))
                    {
                        if (_options.Only != null && !_options.Only.Contains(scenario.Name))
                            continue;
                        if (scenario.Heavy && !_options.Heavy && _options.Only == null)
                            continue;
                        var result = await scenario.Run().ConfigureAwait(false);
                        Console.WriteLine(result.Describe());
                        results.Add(result);
                    }
                }

                AddRatios(results);
                var report = BenchReport.Create(results, _options);
                int exit = 0;
                if (_options.ComparePath != null)
                    exit = BenchReport.Compare(report, BenchReport.Load(_options.ComparePath), _options.BudgetPercent);
                File.WriteAllText(Path.Combine(_options.OutDir, "result.json"), BenchReport.Serialize(report));
                if (_options.WriteBaselinePath != null)
                {
                    File.WriteAllText(_options.WriteBaselinePath, BenchReport.Serialize(report));
                    Console.WriteLine("bench: baseline written to " + _options.WriteBaselinePath);
                }

                return exit;
            }
            finally
            {
                _client?.Dispose();
                StopServer();
            }
        }

        // ---- scenarios ----

        private IEnumerable<Scenario> Scenarios(RawHttp11Client raw)
        {
            var body1k = Slice(1024);
            var body5m = Slice(5 * 1024 * 1024);
            int n = _options.Iterations;
            int large = Math.Max(20, n / 50);

            foreach (var (proto, origin) in new[] { ("h1", _h1), ("h2", _h2) })
            {
                string expected = proto == "h1" ? "HTTP/1.1" : "HTTP/2";
                // Step 2 items 1-2: small buffered round trips, the <= 5 % budget rows.
                yield return Latency(proto + "-get-1k-buffered", true, n, 1024, v => BufferedGetAsync(origin, 1024, expected, v));
                yield return Latency(proto + "-get-32k-buffered", true, n / 2, 32 * 1024, v => BufferedGetAsync(origin, 32 * 1024, expected, v));
                yield return Latency(proto + "-post-1k-buffered", true, n, 1024, v => BufferedEchoAsync(origin, body1k, expected, v));
                // The same small body through the streaming API, and a 1 MiB body for throughput.
                yield return Latency(proto + "-get-1k-streaming", false, n, 1024, v => StreamingGetAsync(origin, 1024, expected, v));
                yield return Latency(proto + "-get-1m-streaming", false, large, 1024 * 1024, v => StreamingGetAsync(origin, 1024 * 1024, expected, v));
                // Step 2 item 3: 5 MB upload, buffered body vs stream body.
                yield return Latency(proto + "-post-5m-buffered", false, Math.Max(10, large / 2), body5m.Length, v => UploadAsync(origin, body5m, stream: false, expected));
                yield return Latency(proto + "-post-5m-stream", false, Math.Max(10, large / 2), body5m.Length, v => UploadAsync(origin, body5m, stream: true, expected));
            }

            // The raw-socket floor for the streaming throughput guardrail: same server, same route, no client.
            yield return Latency("raw-h1-get-1m", false, large, 1024 * 1024, v => raw.GetAsync(1024 * 1024));

            // Step 2 items 4-5 and Step 4: peak managed heap (opt-in with --heavy).
            yield return Memory("h1-get-100m-file-streaming", 3, () => DownloadToFileAsync(_h1, 100 * 1024 * 1024, streaming: true));
            yield return Memory("h1-get-100m-file-buffered", 3, () => DownloadToFileAsync(_h1, 100 * 1024 * 1024, streaming: false));
            yield return Memory("h2-10x10m-one-slow-consumer", 1, ConcurrentSlowConsumerAsync);
        }

        private Scenario Latency(string name, bool budgeted, int iterations, long bytes, Func<bool, Task> op)
        {
            return new Scenario(name, heavy: false, () => MeasureLatencyAsync(name, budgeted, Math.Max(1, iterations), bytes, op));
        }

        private Scenario Memory(string name, int iterations, Func<Task<Dictionary<string, double>>> op)
        {
            return new Scenario(name, heavy: true, () => MeasureMemoryAsync(name, iterations, op));
        }

        private async Task BufferedGetAsync(string origin, int size, string expected, bool verify)
        {
            using var response = await _client.SendBufferedAsync(_client.Get(origin + "/bytes/" + size)).ConfigureAwait(false);
            Check(response.StatusCode, response.Body.Length, size, verify ? response.Headers : null, expected);
            if (verify && !Pattern.Matches(response.Body.ToArray(), 0))
                throw new InvalidDataException("buffered body differs from the pattern");
        }

        private async Task BufferedEchoAsync(string origin, byte[] body, string expected, bool verify)
        {
            using var response = await _client.SendBufferedAsync(_client.Post(origin + "/echo").WithBody(body)).ConfigureAwait(false);
            Check(response.StatusCode, response.Body.Length, body.Length, verify ? response.Headers : null, expected);
            if (verify && !response.Body.ToArray().AsSpan().SequenceEqual(body))
                throw new InvalidDataException("echo body differs");
        }

        private async Task StreamingGetAsync(string origin, int size, string expected, bool verify)
        {
            var response = await _client.SendStreamingAsync(_client.Get(origin + "/bytes/" + size)).ConfigureAwait(false);
            await using (response.ConfigureAwait(false))
            {
                long total = 0;
                int n;
                while ((n = await response.Body.ReadAsync(_readBuffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    if (verify && !Pattern.Matches(_readBuffer.AsSpan(0, n), total))
                        throw new InvalidDataException("streamed body differs near byte " + total);
                    total += n;
                }

                Check(response.StatusCode, total, size, verify ? response.Headers : null, expected);
            }
        }

        private async Task UploadAsync(string origin, byte[] body, bool stream, string expected)
        {
            var request = _client.Post(origin + "/sink");
            if (stream)
                request.WithStreamBody(new MemoryStream(body, writable: false), body.Length);
            else
                request.WithBody(body);
            using var response = await _client.SendBufferedAsync(request).ConfigureAwait(false);
            Check(response.StatusCode, body.Length, body.Length, response.Headers, expected);
            string count = System.Text.Encoding.ASCII.GetString(response.Body.ToArray());
            if (count != body.Length.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("server received " + count + " of " + body.Length + " bytes");
        }

        private async Task<Dictionary<string, double>> DownloadToFileAsync(string origin, int size, bool streaming)
        {
            string file = Path.Combine(_options.OutDir, "download.bin");
            try
            {
                var clock = Stopwatch.StartNew();
                using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    if (streaming)
                    {
                        var response = await _client.SendStreamingAsync(_client.Get(origin + "/bytes/" + size)).ConfigureAwait(false);
                        await using (response.ConfigureAwait(false))
                        {
                            long total = 0;
                            int n;
                            while ((n = await response.Body.ReadAsync(_readBuffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(_readBuffer.AsMemory(0, n)).ConfigureAwait(false);
                                total += n;
                            }

                            Check(response.StatusCode, total, size, null, null);
                        }
                    }
                    else
                    {
                        using var response = await _client.SendBufferedAsync(_client.Get(origin + "/bytes/" + size)).ConfigureAwait(false);
                        Check(response.StatusCode, response.Body.Length, size, null, null);
                        foreach (var segment in response.Body)
                            await output.WriteAsync(segment).ConfigureAwait(false);
                    }
                }

                return new Dictionary<string, double> { ["mb_per_second"] = size / (1024.0 * 1024.0) / clock.Elapsed.TotalSeconds };
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Step 2 item 5: ten 10 MiB HTTP/2 downloads multiplexed on one connection, one consumer pausing 2 ms
        /// per 16 KiB read. The fast streams must finish well before the slow one (per-stream flow control:
        /// a stalled reader does not hold the connection window), and the heap must stay near the per-stream
        /// receive buffers, not the 100 MiB total.
        /// </summary>
        private async Task<Dictionary<string, double>> ConcurrentSlowConsumerAsync()
        {
            const int Streams = 10;
            const int Size = 10 * 1024 * 1024;
            var clock = Stopwatch.StartNew();
            var finished = new double[Streams];
            var tasks = new Task[Streams];
            for (int i = 0; i < Streams; i++)
            {
                int index = i;
                tasks[i] = Task.Run(async () =>
                {
                    var buffer = new byte[ReadBufferBytes];
                    var response = await _client.SendStreamingAsync(_client.Get(_h2 + "/bytes/" + Size)).ConfigureAwait(false);
                    await using (response.ConfigureAwait(false))
                    {
                        long total = 0;
                        int n;
                        while ((n = await response.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                        {
                            total += n;
                            if (index == 0)
                                await Task.Delay(2).ConfigureAwait(false); // the slow consumer, by design
                        }

                        Check(response.StatusCode, total, Size, response.Headers, "HTTP/2");
                    }

                    finished[index] = clock.Elapsed.TotalMilliseconds;
                });
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            double fastMax = finished.Skip(1).Max();
            double slow = finished[0];
            if (fastMax >= slow)
                throw new InvalidDataException("a fast HTTP/2 stream finished after the slow consumer (" + fastMax + " >= " + slow + " ms)");
            return new Dictionary<string, double> { ["fast_streams_max_ms"] = fastMax, ["slow_stream_ms"] = slow };
        }

        // ---- measurement ----

        private async Task<ScenarioResult> MeasureLatencyAsync(string name, bool budgeted, int iterations, long bytes, Func<bool, Task> op)
        {
            // Warm-up: connection, TLS session, pools, the first verified round trip.
            await op(true).ConfigureAwait(false);
            int warmup = Math.Min(_options.Warmup, Math.Max(2, iterations / 4));
            for (int i = 0; i < warmup; i++)
                await op(false).ConfigureAwait(false);

            var all = new List<double>(iterations * _options.Rounds);
            var roundP50 = new List<double>();
            var roundAlloc = new List<double>();
            var samples = new long[iterations];
            long totalTicks = 0;
            for (int round = 0; round < _options.Rounds; round++)
            {
                FullGc();
                long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
                long roundStart = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    await op(false).ConfigureAwait(false);
                    samples[i] = Stopwatch.GetTimestamp() - start;
                }

                totalTicks += Stopwatch.GetTimestamp() - roundStart;
                long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
                roundAlloc.Add((allocAfter - allocBefore) / (double)iterations);
                var us = samples.Select(TicksToMicroseconds).OrderBy(v => v).ToArray();
                roundP50.Add(Percentile(us, 0.50));
                all.AddRange(us);
            }

            all.Sort();
            double seconds = totalTicks / (double)Stopwatch.Frequency;
            return new ScenarioResult
            {
                Name = name,
                Budgeted = budgeted,
                Requests = iterations * _options.Rounds,
                P50Us = Round(Median(roundP50)),
                P95Us = Round(Percentile(all, 0.95)),
                MeanUs = Round(all.Average()),
                AllocBytesPerRequest = Math.Round(Median(roundAlloc)),
                MbPerSecond = bytes >= 1024 * 1024 ? Round(bytes * (double)iterations * _options.Rounds / (1024.0 * 1024.0) / seconds) : (double?)null,
            };
        }

        private static async Task<ScenarioResult> MeasureMemoryAsync(string name, int iterations, Func<Task<Dictionary<string, double>>> op)
        {
            Dictionary<string, double> extra = null;
            long baseline = FullGc();
            long peak = baseline;
            using var stop = new CancellationTokenSource();
            var sampler = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    long heap = GC.GetTotalMemory(false);
                    if (heap > Interlocked.Read(ref peak))
                        Interlocked.Exchange(ref peak, heap);
                    Thread.Sleep(1); // a sampling interval, not synchronization
                }
            })
            { IsBackground = true, Name = "bench-heap-sampler" };
            sampler.Start();
            var clock = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < iterations; i++)
                    extra = await op().ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                sampler.Join();
            }

            var result = new ScenarioResult
            {
                Name = name,
                Requests = iterations,
                MeanUs = Round(clock.Elapsed.TotalMilliseconds * 1000.0 / iterations),
                PeakHeapGrowthMb = Round((Interlocked.Read(ref peak) - baseline) / (1024.0 * 1024.0)),
                Extra = extra,
            };
            if (extra != null && extra.TryGetValue("mb_per_second", out double mbps))
            {
                result.MbPerSecond = Round(mbps);
                extra.Remove("mb_per_second");
            }

            return result;
        }

        private static void AddRatios(List<ScenarioResult> results)
        {
            // The streaming guardrail: TurboHTTP's 1 MiB streamed download vs the raw socket on the same route.
            var raw = results.FirstOrDefault(r => r.Name == "raw-h1-get-1m");
            var streamed = results.FirstOrDefault(r => r.Name == "h1-get-1m-streaming");
            if (raw?.MbPerSecond > 0 && streamed?.MbPerSecond != null)
            {
                streamed.Extra ??= new Dictionary<string, double>();
                streamed.Extra["throughput_vs_raw"] = Math.Round(streamed.MbPerSecond.Value / raw.MbPerSecond.Value, 3);
                Console.WriteLine("bench: h1 1 MiB streaming throughput = " + streamed.Extra["throughput_vs_raw"].ToString("0.000", CultureInfo.InvariantCulture) +
                                  " x the raw-socket floor");
            }
        }

        private static void Check(HttpStatusCode status, long actual, long expected, HttpHeaders headers, string protocol)
        {
            if (status != HttpStatusCode.OK)
                throw new InvalidDataException("status " + (int)status);
            if (actual != expected)
                throw new InvalidDataException("body " + actual + " of " + expected + " bytes");
            if (headers != null && protocol != null && headers.Get("x-bench-protocol") != protocol)
                throw new InvalidDataException("expected " + protocol + ", the server saw " + headers.Get("x-bench-protocol"));
        }

        private static byte[] Slice(int size)
        {
            var body = new byte[size];
            for (int offset = 0; offset < size; offset += Pattern.BlockSize)
                Pattern.Block.AsSpan(0, Math.Min(Pattern.BlockSize, size - offset)).CopyTo(body.AsSpan(offset));
            return body;
        }

        private static long FullGc()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return GC.GetTotalMemory(false);
        }

        private static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

        private static double Percentile(IReadOnlyList<double> sorted, double p) =>
            sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }

        private static double Round(double value) => Math.Round(value, 1);

        // ---- server process ----

        private async Task<(int H1, int Tls)> StartServerAsync()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("serve");
            start.ArgumentList.Add(_options.PkiDir);
            _server = Process.Start(start) ?? throw new InvalidOperationException("could not start the serve process");

            var readLine = _server.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(readLine, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != readLine ||
                readLine.Result == null || !readLine.Result.StartsWith("READY ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the serve process did not report READY");
            }

            var ready = JsonSerializer.Deserialize<Dictionary<string, int>>(readLine.Result.Substring(6));
            return (ready["h1"], ready["tls"]);
        }

        private void StopServer()
        {
            if (_server == null)
                return;
            _server.StandardInput.Close();
            if (!_server.WaitForExit(15000))
                _server.Kill(entireProcessTree: true);
            _server.Dispose();
        }

        private sealed class Scenario
        {
            public Scenario(string name, bool heavy, Func<Task<ScenarioResult>> run)
            {
                Name = name;
                Heavy = heavy;
                Run = run;
            }

            public string Name { get; }
            public bool Heavy { get; }
            public Func<Task<ScenarioResult>> Run { get; }
        }
    }
}
