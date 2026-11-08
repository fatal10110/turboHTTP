using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tools.Soak
{
    /// <summary>
    /// The `run` process: workers draw operations from a seeded mix and send them through one
    /// <see cref="UHttpClient"/> (plus a <see cref="WebSocketClient"/> per WebSocket operation) to the
    /// clean server and to every fault proxy. Checkpoints pause the workers, wait until nothing is in
    /// flight, force a full GC and record the managed heap, the client's socket count and the pooled
    /// objects still outstanding, so every checkpoint compares the same (idle) load level. Pooled byte
    /// buffers (<see cref="ByteArrayPool"/>, #292) are recorded at every checkpoint, but idle pooled
    /// connections legitimately hold their socket and read buffers, so they must be back to the pre-client
    /// count only once the client and transport are disposed.
    /// </summary>
    internal sealed class SoakClient
    {
        private static readonly TimeSpan OpWatchdog = TimeSpan.FromSeconds(120);
        // Above the slowest legitimate operation, below the watchdog: on the lossy profile a 1 MiB HTTP/2
        // echo shares the server's 64 KiB connection window with the other uploads, and every stall also
        // delays the WINDOW_UPDATEs queued behind the downloads, so a few echoes needed more than 30 s
        // (#409). A flow-control wait that never wakes still fails here, as a typed Timeout.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

        private readonly SoakOptions _options;
        private readonly ConcurrentDictionary<string, long> _outcomes = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentQueue<string> _failures = new ConcurrentQueue<string>();
        private readonly List<Dictionary<string, object>> _checkpoints = new List<Dictionary<string, object>>();
        private readonly ConcurrentBag<Task> _stuck = new ConcurrentBag<Task>();
        private readonly Gate _gate = new Gate();
        private Dictionary<string, Ports> _targets;
        private UHttpClient _client;
        private Process _server;
        private StreamWriter _samples;
        private long _ops;
        private long _failureCount;
        private long _unobserved;
        private long _byteBuffersBefore;
        private int _inFlight;
        private volatile bool _stop;

        public SoakClient(SoakOptions options)
        {
            _options = options;
        }

        public async Task<int> RunAsync()
        {
            Directory.CreateDirectory(_options.OutDir);
            _samples = new StreamWriter(Path.Combine(_options.OutDir, "samples.csv")) { AutoFlush = true };
            _samples.WriteLine("seconds,ops,failures,heap_bytes,sockets,h2_stream_active,parsed_active,scratch_active,byte_buffers_held,gen0,gen1,gen2");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Interlocked.Increment(ref _unobserved);
                var root = e.Exception.GetBaseException();
                Fail("unobserved task exception (a faulted task nobody awaited): " + Describe(root) +
                     (root.StackTrace != null ? "\n" + root.StackTrace : ""));
                e.SetObserved();
            };

            var allTargets = await StartServerAsync().ConfigureAwait(false);
            _targets = _options.Profiles.ToDictionary(p => p, p => allTargets.TryGetValue(p, out var ports)
                ? ports
                : throw new ArgumentException("unknown profile " + p));
            int socketsBefore = SocketCount();

            // No Unity bootstrap runs in a plain .NET process, so the raw-socket WebSocket transport registers here.
            RawSocketWebSocketTransport.EnsureRegistered();
            // Byte buffers the library holds before the client exists (none expected); the harness's own rents
            // in Ops.cs use ArrayPool<byte>.Shared directly and are not counted.
            _byteBuffersBefore = ByteArrayPool.GetDiagnostics().ActiveCount;
            var transport = new RawSocketTransport();
            _client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                AllowCleartext = true, // loopback HTTP/1.1 and ws:// rows; TLS rows verify the per-run root
                DefaultTimeout = RequestTimeout,
                FollowRedirects = false,
            });

            int warmup = _options.WarmupSeconds >= 0 ? _options.WarmupSeconds : Math.Min(60, _options.DurationSeconds / 5);
            Console.WriteLine("soak: " + _options.DurationSeconds + " s, " + _options.Workers + " workers, seed " + _options.Seed +
                              ", warm-up " + warmup + " s, profiles " + string.Join(",", _options.Profiles));

            var clock = Stopwatch.StartNew();
            var workers = new List<Task>();
            for (int w = 0; w < _options.Workers; w++)
            {
                int seed = _options.Seed * 1000 + w;
                workers.Add(Task.Run(() => WorkerAsync(seed)));
            }

            double nextSample = _options.SampleSeconds;
            double nextCheckpoint = warmup;
            bool baselineTaken = false;
            while (clock.Elapsed.TotalSeconds < _options.DurationSeconds)
            {
                await Task.Delay(200).ConfigureAwait(false);
                double now = clock.Elapsed.TotalSeconds;
                if (now >= nextSample)
                {
                    Sample(now);
                    nextSample += _options.SampleSeconds;
                }

                if (now >= nextCheckpoint && now < _options.DurationSeconds - 5)
                {
                    await CheckpointAsync(baselineTaken ? "periodic" : "baseline", clock).ConfigureAwait(false);
                    baselineTaken = true;
                    nextCheckpoint = clock.Elapsed.TotalSeconds + _options.CheckpointSeconds;
                }
            }

            _stop = true;
            _gate.Open();
            var joined = Task.WhenAll(workers);
            if (await Task.WhenAny(joined, Task.Delay(OpWatchdog + OpWatchdog)).ConfigureAwait(false) != joined)
                Fail("workers did not stop within " + (2 * OpWatchdog.TotalSeconds) + " s");
            await CheckpointAsync("final", clock).ConfigureAwait(false);

            _client.Dispose();
            transport.Dispose();
            int socketsAfter = await WaitForAsync(SocketCount, v => v <= socketsBefore).ConfigureAwait(false);
            // Connection teardown returns the socket-channel and read-loop buffers as the loops exit.
            long byteBuffersAfter = await WaitForAsync(ByteBuffersHeld, v => v == 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var serverStats = await WaitForAsync(ReadServerStats,
                s => s["h1Open"] == 0 && s["tlsOpen"] == 0 && s["proxyOpen"] == 0, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            StopServer();

            return Report(clock.Elapsed, warmup, baselineTaken, socketsBefore, socketsAfter, byteBuffersAfter, serverStats);
        }

        // ---- workers ----

        private async Task WorkerAsync(int seed)
        {
            var random = new Random(seed);
            while (!_stop)
            {
                Interlocked.Increment(ref _inFlight);
                if (!_gate.IsOpen)
                {
                    Interlocked.Decrement(ref _inFlight);
                    await _gate.WaitAsync().ConfigureAwait(false);
                    continue;
                }

                try
                {
                    string profile = _options.Profiles[random.Next(_options.Profiles.Length)];
                    var op = Ops.Pick(random);
                    int opSeed = random.Next();
                    using var watchdog = new CancellationTokenSource(OpWatchdog);
                    var task = RunOpAsync(op, profile, new Random(opSeed), watchdog.Token);
                    if (await Task.WhenAny(task, Task.Delay(OpWatchdog + TimeSpan.FromSeconds(30))).ConfigureAwait(false) != task)
                    {
                        _stuck.Add(task);
                        Count(op, profile, "hang");
                        Fail(op + "@" + profile + ": ignored cancellation for " + (OpWatchdog.TotalSeconds + 30) + " s");
                        continue;
                    }

                    await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Fail("worker: " + Describe(ex));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    Interlocked.Increment(ref _ops);
                }
            }
        }

        private async Task RunOpAsync(string op, string profile, Random random, CancellationToken watchdog)
        {
            var ports = _targets[profile];
            bool injects = Ops.InjectsFailures(profile);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(watchdog);
            try
            {
                string outcome = await Ops.RunAsync(op, _client, ports, random, cancel, watchdog).ConfigureAwait(false);
                Count(op, profile, outcome);
            }
            catch (VerificationException ex)
            {
                Count(op, profile, "corrupt");
                Fail(op + "@" + profile + ": " + ex.Message);
            }
            catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
            {
                Count(op, profile, "hang");
                Fail(op + "@" + profile + ": still running after " + OpWatchdog.TotalSeconds + " s");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && Ops.Cancels(op))
            {
                Count(op, profile, "cancelled");
            }
            catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Cancelled && cancel.IsCancellationRequested && Ops.Cancels(op))
            {
                Count(op, profile, "cancelled");
            }
            catch (Exception ex) when (injects && (ex is UHttpException || ex is WebSocketException ||
                                                   (ex is InvalidOperationException && Ops.IsWebSocket(op))))
            {
                // The reset profile tears connections down on purpose: a typed failure is the contract.
                Count(op, profile, "fault");
            }
            catch (Exception ex)
            {
                Count(op, profile, "error");
                Fail(op + "@" + profile + ": " + Describe(ex) + "\n" + ex);
            }
        }

        // ---- checkpoints and samples ----

        private async Task CheckpointAsync(string label, Stopwatch clock)
        {
            _gate.Close();
            int inFlight = await WaitForAsync(() => Volatile.Read(ref _inFlight), v => v == 0, TimeSpan.FromSeconds(300)).ConfigureAwait(false);
            if (inFlight != 0)
                Fail(label + " checkpoint: " + inFlight + " operation(s) still in flight after 300 s");

            // A read loop may still hold a stream lease for a few microseconds after the caller saw the end.
            var pools = await WaitForAsync(PoolActive, p => p.All(kv => kv.Value == 0), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            long heap = ForcedGcHeap();
            int sockets = SocketCount();
            long byteBuffers = ByteBuffersHeld();
            var serverStats = ReadServerStats();
            var checkpoint = new Dictionary<string, object>
            {
                ["label"] = label,
                ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                ["ops"] = Interlocked.Read(ref _ops),
                ["heap_bytes"] = heap,
                ["sockets"] = sockets,
                ["pool_active"] = pools,
                ["byte_buffers_held"] = byteBuffers,
                ["server"] = serverStats,
            };
            _checkpoints.Add(checkpoint);
            Console.WriteLine("checkpoint " + label + ": t=" + checkpoint["seconds"] + "s ops=" + checkpoint["ops"] +
                              " heap=" + Mb(heap) + "MB sockets=" + sockets + " pools=" + JsonSerializer.Serialize(pools) +
                              " byte_buffers=" + byteBuffers + " server=" + JsonSerializer.Serialize(serverStats));
            foreach (var kv in pools.Where(kv => kv.Value != 0))
                Fail(label + " checkpoint: " + kv.Value + " pooled " + kv.Key + " not returned with nothing in flight");
            if (sockets > _options.MaxSockets)
                Fail(label + " checkpoint: " + sockets + " client sockets open (max " + _options.MaxSockets + ")");
            _gate.Open();
        }

        private void Sample(double seconds)
        {
            var pools = PoolActive();
            long heap = GC.GetTotalMemory(false);
            int sockets = SocketCount();
            _samples.WriteLine(string.Join(",", new object[]
            {
                Math.Round(seconds, 1).ToString(CultureInfo.InvariantCulture), Interlocked.Read(ref _ops), Interlocked.Read(ref _failureCount),
                heap, sockets, pools["Http2Stream"], pools["ParsedResponse"], pools["HeaderParseScratch"], ByteBuffersHeld(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            }));
            Console.WriteLine("t=" + Math.Round(seconds) + "s ops=" + Interlocked.Read(ref _ops) + " failures=" + Interlocked.Read(ref _failureCount) +
                              " heap=" + Mb(heap) + "MB sockets=" + sockets + " in-flight h2 streams=" + pools["Http2Stream"]);
        }

        private static Dictionary<string, long> PoolActive() => new Dictionary<string, long>
        {
            ["Http2Stream"] = Http2StreamPool.GetDiagnostics().ActiveCount,
            ["ParsedResponse"] = ParsedResponsePool.GetDiagnostics().ActiveCount,
            ["HeaderParseScratch"] = HeaderParseScratchPool.GetDiagnostics().ActiveCount,
        };

        // Library byte buffers outstanding beyond the count before the client was created (#292).
        private long ByteBuffersHeld() => ByteArrayPool.GetDiagnostics().ActiveCount - _byteBuffersBefore;

        private static long ForcedGcHeap()
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            return GC.GetTotalMemory(true);
        }

        /// <summary>Client sockets: /proc/self/fd entries that are sockets (Linux); -1 elsewhere.</summary>
        private static int SocketCount()
        {
            if (!Directory.Exists("/proc/self/fd"))
                return -1;
            int count = 0;
            foreach (var fd in Directory.EnumerateFiles("/proc/self/fd"))
            {
                try
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target != null && target.StartsWith("socket:", StringComparison.Ordinal))
                        count++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return count;
        }

        private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done, TimeSpan? limit = null)
        {
            var deadline = Stopwatch.StartNew();
            var value = read();
            while (!done(value) && deadline.Elapsed < (limit ?? TimeSpan.FromSeconds(15)))
            {
                await Task.Delay(50).ConfigureAwait(false);
                value = read();
            }

            return value;
        }

        // ---- server process ----

        private async Task<Dictionary<string, Ports>> StartServerAsync()
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
            start.ArgumentList.Add("--seed");
            start.ArgumentList.Add(_options.Seed.ToString(CultureInfo.InvariantCulture));
            _server = Process.Start(start) ?? throw new InvalidOperationException("could not start the serve process");

            var readLine = _server.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(readLine, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != readLine ||
                readLine.Result == null || !readLine.Result.StartsWith("READY ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the serve process did not report READY");
            }

            var ready = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(readLine.Result.Substring(6));
            return ready.ToDictionary(kv => kv.Key, kv => new Ports(kv.Value["h1"], kv.Value["tls"]));
        }

        private Dictionary<string, long> ReadServerStats()
        {
            _server.StandardInput.WriteLine("stats");
            _server.StandardInput.Flush();
            string line;
            while ((line = _server.StandardOutput.ReadLine()) != null)
            {
                if (line.StartsWith("STATS ", StringComparison.Ordinal))
                    return JsonSerializer.Deserialize<Dictionary<string, long>>(line.Substring(6));
            }

            throw new InvalidOperationException("the serve process exited");
        }

        private void StopServer()
        {
            _server.StandardInput.Close();
            if (!_server.WaitForExit(15000))
                _server.Kill(entireProcessTree: true);
        }

        // ---- report ----

        private int Report(TimeSpan elapsed, int warmup, bool baselineTaken, int socketsBefore, int socketsAfter,
            long byteBuffersAfter, Dictionary<string, long> serverAfter)
        {
            var problems = new List<string>();
            long baselineHeap = baselineTaken ? (long)_checkpoints.First()["heap_bytes"] : -1;
            long finalHeap = (long)_checkpoints.Last()["heap_bytes"];
            long growth = baselineTaken ? finalHeap - baselineHeap : 0;
            long peakHeap = _checkpoints.Max(c => (long)c["heap_bytes"]);
            if (!baselineTaken)
                problems.Add("no baseline checkpoint (duration shorter than the warm-up)");
            if (growth > _options.MaxGrowthMb * 1024 * 1024)
                problems.Add("managed heap grew " + Mb(growth) + " MB (max " + _options.MaxGrowthMb + " MB)");
            if (Interlocked.Read(ref _ops) == 0)
                problems.Add("no operation ran");
            if (Interlocked.Read(ref _failureCount) > 0)
                problems.Add(Interlocked.Read(ref _failureCount) + " failure(s)");
            if (socketsBefore >= 0 && socketsAfter > socketsBefore)
                problems.Add((socketsAfter - socketsBefore) + " client socket(s) still open after dispose");
            if (byteBuffersAfter != 0)
                problems.Add(byteBuffersAfter + " pooled byte buffer(s) not returned after the client and transport were disposed " +
                             "(ByteArrayPool rents minus returns; negative means a return without a counted rent)");
            // The fault proxies close a connection as soon as either side does, so an open proxied connection
            // means the client kept its socket. Kestrel may tear its own side down later (it drains HTTP/2
            // handlers); its count is the oracle only where the client's own socket count is unavailable.
            if (serverAfter["proxyOpen"] != 0 ||
                (socketsBefore < 0 && serverAfter["h1Open"] + serverAfter["tlsOpen"] != 0))
                problems.Add("server still sees open connections after the client was disposed: " + JsonSerializer.Serialize(serverAfter));
            if (!_stuck.IsEmpty)
                problems.Add(_stuck.Count + " operation(s) never finished");

            var result = new Dictionary<string, object>
            {
                ["verdict"] = problems.Count == 0 ? "pass" : "fail",
                ["problems"] = problems,
                ["duration_seconds"] = Math.Round(elapsed.TotalSeconds, 1),
                ["warmup_seconds"] = warmup,
                ["workers"] = _options.Workers,
                ["seed"] = _options.Seed,
                ["profiles"] = _options.Profiles,
                ["ops"] = Interlocked.Read(ref _ops),
                ["heap_baseline_bytes"] = baselineHeap,
                ["heap_final_bytes"] = finalHeap,
                ["heap_growth_bytes"] = growth,
                ["heap_peak_checkpoint_bytes"] = peakHeap,
                ["max_growth_mb"] = _options.MaxGrowthMb,
                ["sockets_before"] = socketsBefore,
                ["sockets_after_dispose"] = socketsAfter,
                ["byte_buffers_after_dispose"] = byteBuffersAfter,
                ["server_after_dispose"] = serverAfter,
                ["unobserved_task_exceptions"] = Interlocked.Read(ref _unobserved),
                ["checkpoints"] = _checkpoints,
                ["outcomes"] = new SortedDictionary<string, long>(_outcomes),
                ["failures"] = _failures.Take(50).ToList(),
                ["dotnet"] = Environment.Version.ToString(),
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_options.OutDir, "result.json"), json);

            Console.WriteLine("soak: " + result["verdict"] + " after " + result["duration_seconds"] + " s, " + result["ops"] + " operations; heap " +
                              Mb(baselineHeap) + " -> " + Mb(finalHeap) + " MB (growth " + Mb(growth) + " MB), sockets " + socketsBefore + " -> " + socketsAfter);
            foreach (var kv in new SortedDictionary<string, long>(_outcomes))
                Console.WriteLine("  " + kv.Key + " " + kv.Value);
            foreach (var problem in problems)
                Console.WriteLine("FAIL: " + problem);
            foreach (var failure in _failures.Take(20))
                Console.WriteLine("  " + failure);
            return problems.Count == 0 ? 0 : 1;
        }

        private void Count(string op, string profile, string outcome) =>
            _outcomes.AddOrUpdate(op + "@" + profile + ":" + outcome, 1, (_, v) => v + 1);

        private void Fail(string message)
        {
            Interlocked.Increment(ref _failureCount);
            if (_failures.Count < 200)
                _failures.Enqueue(message);
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture);

        internal static string Describe(Exception ex)
        {
            if (ex is UHttpException http)
                return "UHttpException(" + http.HttpError.Type + "): " + http.Message;
            if (ex is WebSocketException ws)
                return "WebSocketException(" + ws.Error + "): " + ws.Message;
            return ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>Async gate the workers pass between operations; closed while a checkpoint measures.</summary>
        private sealed class Gate
        {
            private volatile TaskCompletionSource<bool> _open = Completed();

            public bool IsOpen => _open.Task.IsCompleted;

            public Task WaitAsync() => _open.Task;

            public void Open() => _open.TrySetResult(true);

            public void Close()
            {
                if (_open.Task.IsCompleted)
                    _open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private static TaskCompletionSource<bool> Completed()
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetResult(true);
                return tcs;
            }
        }
    }

    internal readonly struct Ports
    {
        public readonly int H1;
        public readonly int Tls;

        public Ports(int h1, int tls)
        {
            H1 = h1;
            Tls = tls;
        }
    }

    internal sealed class VerificationException : Exception
    {
        public VerificationException(string message) : base(message)
        {
        }
    }
}
