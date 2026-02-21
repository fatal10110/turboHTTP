using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Performance
{
    [TestFixture]
    [Category("Benchmark")]
    public sealed class Phase19AllocationGateTests
    {
        private const int RunsPerScenario = 3;
        private const int WarmupIterations = 32;
        private const string ThresholdPercentEnvVar = "TURBOHTTP_ALLOCATION_REGRESSION_THRESHOLD_PERCENT";
        private const string BaselineRecordEnvVar = "TURBOHTTP_ALLOCATION_BASELINE_RECORD";
        private const string BaselineOutputEnvVar = "TURBOHTTP_ALLOCATION_BASELINE_OUTPUT";

        [Test]
        public void AllocationScenarios_RespectBaselineBudget()
        {
            Task.Run(async () =>
            {
                var baselinePath = ResolveBaselinePath();
                var baseline = LoadBaselineDocument(baselinePath);

                var measurements = new List<ScenarioMeasurement>
                {
                    await MeasureHttp11WarmRequestScenarioAsync().ConfigureAwait(false),
                    await MeasureMiddlewareFastPathScenarioAsync().ConfigureAwait(false),
                    await MeasureHttp2PoolSingleCompletionScenarioAsync().ConfigureAwait(false),
                    await MeasureHttp2PoolBurstScenarioAsync().ConfigureAwait(false)
                };

                foreach (var measurement in measurements)
                {
                    TestContext.Progress.WriteLine(
                        "[Phase19][Allocation] " + measurement.Name +
                        " bytes/op=" + measurement.BytesPerOperation +
                        " gen0=" + measurement.Gen0Collections +
                        " sampleCount=" + measurement.SampleCount +
                        " medianDurationMs=" + measurement.MedianDurationMs.ToString("F3"));
                }

                if (IsBaselineRecordMode())
                {
                    var outputPath = ResolveBaselineOutputPath(baselinePath);
                    WriteObservedBaseline(outputPath, baseline.DefaultThresholdPercent, measurements);
                    TestContext.Progress.WriteLine(
                        "[Phase19][Allocation] Recorded observed baselines to: " + outputPath);
                    return;
                }

                var thresholdPercent = ResolveThresholdPercent(baseline.DefaultThresholdPercent);

                foreach (var measurement in measurements)
                {
                    if (!baseline.Scenarios.TryGetValue(measurement.Name, out var scenario))
                    {
                        Assert.Fail(
                            "Missing baseline scenario '" + measurement.Name + "' in " + baselinePath + ".");
                    }

                    Assert.GreaterOrEqual(
                        measurement.SampleCount,
                        scenario.MinSamples,
                        "Scenario '" + measurement.Name + "' ran too few samples.");

                    var allowedBytesPerOp = (long)Math.Ceiling(
                        scenario.MaxBytesPerOperation * (1.0 + (thresholdPercent / 100.0)));

                    Assert.LessOrEqual(
                        measurement.BytesPerOperation,
                        allowedBytesPerOp,
                        "Allocation regression in scenario '" + measurement.Name + "'. " +
                        "Observed=" + measurement.BytesPerOperation + " bytes/op, " +
                        "Baseline=" + scenario.MaxBytesPerOperation + " bytes/op, " +
                        "Threshold=" + thresholdPercent + "%.");
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ThroughputScenarios_ReportRpsAndLatency()
        {
            Task.Run(async () =>
            {
                var sequential = await RunHttp11SequentialThroughputAsync().ConfigureAwait(false);
                var concurrent = await RunHttp11ConcurrentThroughputAsync().ConfigureAwait(false);
                var multiplexed = await RunHttp2PoolMultiplexedThroughputAsync().ConfigureAwait(false);

                ReportThroughput(sequential);
                ReportThroughput(concurrent);
                ReportThroughput(multiplexed);

                Assert.AreEqual(0, sequential.ErrorCount, "Sequential throughput scenario reported errors.");
                Assert.AreEqual(0, concurrent.ErrorCount, "Concurrent throughput scenario reported errors.");
                Assert.AreEqual(0, multiplexed.ErrorCount, "Multiplexed throughput scenario reported errors.");

                Assert.Greater(sequential.RequestsPerSecond, 0.0d);
                Assert.Greater(concurrent.RequestsPerSecond, 0.0d);
                Assert.Greater(multiplexed.RequestsPerSecond, 0.0d);
            }).GetAwaiter().GetResult();
        }

        private static async Task<ScenarioMeasurement> MeasureHttp11WarmRequestScenarioAsync()
        {
            const int sampleCount = 384;

            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            return await MeasureScenarioAsync(
                "http11_warm_request_mock_transport",
                sampleCount,
                async _ =>
                {
                    using var response = await client.Get("https://phase19.local/http11")
                        .SendAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Unexpected non-success in HTTP/1.1 warm request scenario.");
                }).ConfigureAwait(false);
        }

        private static async Task<ScenarioMeasurement> MeasureMiddlewareFastPathScenarioAsync()
        {
            const int sampleCount = 320;
            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            var middleware = new List<IHttpMiddleware>(8)
            {
                new HeaderTagMiddleware("X-P19-A"),
                new HeaderTagMiddleware("X-P19-B"),
                new HeaderTagMiddleware("X-P19-C"),
                new HeaderTagMiddleware("X-P19-D"),
                new HeaderTagMiddleware("X-P19-E"),
                new HeaderTagMiddleware("X-P19-F"),
                new HeaderTagMiddleware("X-P19-G"),
                new HeaderTagMiddleware("X-P19-H")
            };

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false,
                Middlewares = middleware
            });

            return await MeasureScenarioAsync(
                "middleware_chain_sync_fast_path_mock_transport",
                sampleCount,
                async _ =>
                {
                    using var response = await client.Get("https://phase19.local/middleware")
                        .SendAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Unexpected non-success in middleware fast-path scenario.");
                }).ConfigureAwait(false);
        }

        private static async Task<ScenarioMeasurement> MeasureHttp2PoolSingleCompletionScenarioAsync()
        {
            const int sampleCount = 1024;
            var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 256);

            return await MeasureScenarioAsync(
                "http2_poolable_source_single_completion",
                sampleCount,
                async _ =>
                {
                    var source = pool.Rent();
                    var pending = source.CreateValueTask();
                    source.SetResult(true);
                    if (!await pending.ConfigureAwait(false))
                        throw new InvalidOperationException("Unexpected false completion from poolable source.");
                }).ConfigureAwait(false);
        }

        private static async Task<ScenarioMeasurement> MeasureHttp2PoolBurstScenarioAsync()
        {
            const int sampleCount = 160;
            const int burstSize = 100;

            var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 256);
            var sources = new PoolableValueTaskSource<bool>[burstSize];
            var pending = new ValueTask<bool>[burstSize];

            return await MeasureScenarioAsync(
                "http2_poolable_source_multiplexed_burst_100",
                sampleCount,
                async _ =>
                {
                    for (int i = 0; i < burstSize; i++)
                    {
                        sources[i] = pool.Rent();
                        pending[i] = sources[i].CreateValueTask();
                    }

                    for (int i = 0; i < burstSize; i++)
                    {
                        sources[i].SetResult(true);
                    }

                    for (int i = 0; i < burstSize; i++)
                    {
                        if (!await pending[i].ConfigureAwait(false))
                            throw new InvalidOperationException("Unexpected false completion in burst scenario.");
                    }
                }).ConfigureAwait(false);
        }

        private static async Task<ThroughputMeasurement> RunHttp11SequentialThroughputAsync()
        {
            const int requestCount = 1000;
            var latenciesMs = new double[requestCount];
            var errors = 0;

            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            var total = Stopwatch.StartNew();
            for (int i = 0; i < requestCount; i++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var response = await client.Get("https://phase19.local/throughput-sequential")
                        .SendAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        errors++;
                }
                catch
                {
                    errors++;
                }
                finally
                {
                    sw.Stop();
                    latenciesMs[i] = sw.Elapsed.TotalMilliseconds;
                }
            }
            total.Stop();

            return BuildThroughputMeasurement(
                "http11_sequential_mock_transport",
                requestCount,
                errors,
                total.Elapsed,
                latenciesMs);
        }

        private static async Task<ThroughputMeasurement> RunHttp11ConcurrentThroughputAsync()
        {
            const int totalRequests = 1000;
            const int workerCount = 50;
            var latenciesMs = new double[totalRequests];
            var errors = 0;
            var next = -1;

            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            var workers = new Task[workerCount];
            var total = Stopwatch.StartNew();
            for (int worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next);
                        if (index >= totalRequests)
                            break;

                        var sw = Stopwatch.StartNew();
                        try
                        {
                            using var response = await client.Get("https://phase19.local/throughput-concurrent")
                                .SendAsync()
                                .ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                                Interlocked.Increment(ref errors);
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                        finally
                        {
                            sw.Stop();
                            latenciesMs[index] = sw.Elapsed.TotalMilliseconds;
                        }
                    }
                });
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            total.Stop();

            return BuildThroughputMeasurement(
                "http11_concurrent_mock_transport",
                totalRequests,
                errors,
                total.Elapsed,
                latenciesMs);
        }

        private static async Task<ThroughputMeasurement> RunHttp2PoolMultiplexedThroughputAsync()
        {
            const int burstCount = 200;
            const int streamsPerBurst = 100;
            var latenciesMs = new double[burstCount];
            var errors = 0;

            var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 256);
            var sources = new PoolableValueTaskSource<bool>[streamsPerBurst];
            var pending = new ValueTask<bool>[streamsPerBurst];

            var total = Stopwatch.StartNew();
            for (int burst = 0; burst < burstCount; burst++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i < streamsPerBurst; i++)
                    {
                        sources[i] = pool.Rent();
                        pending[i] = sources[i].CreateValueTask();
                    }

                    for (int i = 0; i < streamsPerBurst; i++)
                    {
                        sources[i].SetResult(true);
                    }

                    for (int i = 0; i < streamsPerBurst; i++)
                    {
                        if (!await pending[i].ConfigureAwait(false))
                            errors++;
                    }
                }
                catch
                {
                    errors++;
                }
                finally
                {
                    sw.Stop();
                    latenciesMs[burst] = sw.Elapsed.TotalMilliseconds;
                }
            }
            total.Stop();

            return BuildThroughputMeasurement(
                "http2_poolable_source_multiplexed_throughput",
                burstCount * streamsPerBurst,
                errors,
                total.Elapsed,
                latenciesMs);
        }

        private static ThroughputMeasurement BuildThroughputMeasurement(
            string name,
            int operationCount,
            int errorCount,
            TimeSpan elapsed,
            double[] latenciesMs)
        {
            var safeElapsedSeconds = Math.Max(0.000001, elapsed.TotalSeconds);
            var successfulOperations = Math.Max(0, operationCount - errorCount);
            var rps = successfulOperations / safeElapsedSeconds;
            var p50 = ComputePercentile(latenciesMs, 50.0);
            var p99 = ComputePercentile(latenciesMs, 99.0);

            return new ThroughputMeasurement(name, operationCount, errorCount, rps, p50, p99);
        }

        private static void ReportThroughput(ThroughputMeasurement measurement)
        {
            TestContext.Progress.WriteLine(
                "[Phase19][Throughput] " + measurement.Name +
                " ops=" + measurement.OperationCount +
                " errors=" + measurement.ErrorCount +
                " rps=" + measurement.RequestsPerSecond.ToString("F2") +
                " p50ms=" + measurement.P50LatencyMs.ToString("F3") +
                " p99ms=" + measurement.P99LatencyMs.ToString("F3"));
        }

        private static async Task<ScenarioMeasurement> MeasureScenarioAsync(
            string name,
            int sampleCount,
            Func<int, ValueTask> operation)
        {
            if (sampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            var bytesPerOpSamples = new long[RunsPerScenario];
            var gen0Samples = new int[RunsPerScenario];
            var durationSamplesMs = new double[RunsPerScenario];

            for (int run = 0; run < RunsPerScenario; run++)
            {
                for (int i = 0; i < WarmupIterations; i++)
                    await operation(i).ConfigureAwait(false);

                ForceGc();
                var beforeBytes = GC.GetTotalMemory(true);
                var beforeGen0 = GC.CollectionCount(0);

                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < sampleCount; i++)
                    await operation(i).ConfigureAwait(false);
                stopwatch.Stop();

                ForceGc();
                var afterBytes = GC.GetTotalMemory(true);
                var afterGen0 = GC.CollectionCount(0);

                var bytesDelta = Math.Max(0L, afterBytes - beforeBytes);
                bytesPerOpSamples[run] = bytesDelta / sampleCount;
                gen0Samples[run] = Math.Max(0, afterGen0 - beforeGen0);
                durationSamplesMs[run] = stopwatch.Elapsed.TotalMilliseconds;
            }

            Array.Sort(bytesPerOpSamples);
            Array.Sort(gen0Samples);
            Array.Sort(durationSamplesMs);

            var medianIndex = RunsPerScenario / 2;
            return new ScenarioMeasurement(
                name,
                sampleCount,
                bytesPerOpSamples[medianIndex],
                gen0Samples[medianIndex],
                durationSamplesMs[medianIndex]);
        }

        private static double ComputePercentile(double[] samples, double percentile)
        {
            if (samples == null || samples.Length == 0)
                return 0;

            var ordered = (double[])samples.Clone();
            Array.Sort(ordered);

            if (ordered.Length == 1)
                return ordered[0];

            var position = (percentile / 100.0) * (ordered.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return ordered[lower];

            var weight = position - lower;
            return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
        }

        private static BaselineDocument LoadBaselineDocument(string path)
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
                throw new InvalidOperationException("Failed to deserialize baseline JSON: " + path);

            var baseline = new BaselineDocument
            {
                DefaultThresholdPercent = ReadInt(root, "defaultThresholdPercent", 10),
                Scenarios = new Dictionary<string, BaselineScenario>(StringComparer.Ordinal)
            };

            var scenarioObjects = ReadList(root, "scenarios");
            foreach (var scenarioObject in scenarioObjects)
            {
                var scenarioMap = scenarioObject as Dictionary<string, object>;
                if (scenarioMap == null)
                    throw new InvalidOperationException("Scenario entry is not an object in baseline JSON.");

                var name = ReadString(scenarioMap, "name");
                baseline.Scenarios[name] = new BaselineScenario
                {
                    Name = name,
                    MaxBytesPerOperation = ReadLong(scenarioMap, "maxBytesPerOperation"),
                    MinSamples = ReadInt(scenarioMap, "minSamples", 1)
                };
            }

            return baseline;
        }

        private static string ResolveBaselinePath()
        {
            var candidates = new[]
            {
                Path.Combine("Tests", "Benchmarks", "phase19-allocation-baselines.json"),
                Path.Combine("Packages", "com.turbohttp.complete", "Tests", "Benchmarks", "phase19-allocation-baselines.json")
            };

            var cwd = Directory.GetCurrentDirectory();
            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(Path.Combine(cwd, candidate));
                if (File.Exists(fullPath))
                    return fullPath;
            }

            throw new FileNotFoundException(
                "Could not locate phase19 allocation baseline JSON. " +
                "Checked: " + string.Join(", ", candidates));
        }

        private static bool IsBaselineRecordMode()
        {
            var flag = Environment.GetEnvironmentVariable(BaselineRecordEnvVar);
            return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveBaselineOutputPath(string baselinePath)
        {
            var configured = Environment.GetEnvironmentVariable(BaselineOutputEnvVar);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            var directory = Path.GetDirectoryName(baselinePath);
            if (string.IsNullOrEmpty(directory))
                directory = Directory.GetCurrentDirectory();

            return Path.Combine(directory, "phase19-allocation-baselines.observed.json");
        }

        private static int ResolveThresholdPercent(int defaultThresholdPercent)
        {
            var configured = Environment.GetEnvironmentVariable(ThresholdPercentEnvVar);
            if (string.IsNullOrWhiteSpace(configured))
                return defaultThresholdPercent;

            if (int.TryParse(configured, out var threshold) && threshold >= 0)
                return threshold;

            return defaultThresholdPercent;
        }

        private static void WriteObservedBaseline(
            string outputPath,
            int defaultThresholdPercent,
            IReadOnlyList<ScenarioMeasurement> measurements)
        {
            var scenarioList = new List<object>(measurements.Count);
            for (int i = 0; i < measurements.Count; i++)
            {
                var measurement = measurements[i];
                scenarioList.Add(new Dictionary<string, object>
                {
                    { "name", measurement.Name },
                    { "maxBytesPerOperation", measurement.BytesPerOperation },
                    { "minSamples", measurement.SampleCount },
                    { "notes", "Recorded via TURBOHTTP_ALLOCATION_BASELINE_RECORD=1" }
                });
            }

            var root = new Dictionary<string, object>
            {
                { "version", 1 },
                { "capturedAtUtc", DateTime.UtcNow.ToString("o") },
                { "defaultThresholdPercent", defaultThresholdPercent },
                { "scenarios", scenarioList }
            };

            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            var json = JsonSerializer.Serialize(root);
            File.WriteAllText(outputPath, json);
        }

        private static List<object> ReadList(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || !(value is List<object> list))
                throw new InvalidOperationException("Missing or invalid list property '" + key + "'.");
            return list;
        }

        private static string ReadString(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
                throw new InvalidOperationException("Missing string property '" + key + "'.");

            var str = value as string;
            if (string.IsNullOrEmpty(str))
                throw new InvalidOperationException("Property '" + key + "' must be a non-empty string.");
            return str;
        }

        private static int ReadInt(Dictionary<string, object> map, string key, int defaultValue)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
                return defaultValue;
            return ConvertToInt(value, key);
        }

        private static long ReadLong(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
                throw new InvalidOperationException("Missing numeric property '" + key + "'.");
            return ConvertToLong(value, key);
        }

        private static int ConvertToInt(object value, string propertyName)
        {
            switch (value)
            {
                case int i:
                    return i;
                case long l:
                    return checked((int)l);
                case double d:
                    return checked((int)Math.Round(d));
                case string s when int.TryParse(s, out var parsed):
                    return parsed;
                default:
                    throw new InvalidOperationException(
                        "Property '" + propertyName + "' is not an int-compatible value.");
            }
        }

        private static long ConvertToLong(object value, string propertyName)
        {
            switch (value)
            {
                case int i:
                    return i;
                case long l:
                    return l;
                case double d:
                    return checked((long)Math.Round(d));
                case string s when long.TryParse(s, out var parsed):
                    return parsed;
                default:
                    throw new InvalidOperationException(
                        "Property '" + propertyName + "' is not a long-compatible value.");
            }
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private sealed class HeaderTagMiddleware : IHttpMiddleware
        {
            private readonly string _headerName;

            public HeaderTagMiddleware(string headerName)
            {
                _headerName = headerName;
            }

            public ValueTask<UHttpResponse> InvokeAsync(
                UHttpRequest request,
                RequestContext context,
                HttpPipelineDelegate next,
                CancellationToken ct)
            {
                request.Headers.Set(_headerName, "1");
                return next(request, context, ct);
            }
        }

        private sealed class BaselineDocument
        {
            public int DefaultThresholdPercent;
            public Dictionary<string, BaselineScenario> Scenarios;
        }

        private sealed class BaselineScenario
        {
            public string Name;
            public long MaxBytesPerOperation;
            public int MinSamples;
        }

        private readonly struct ScenarioMeasurement
        {
            public ScenarioMeasurement(
                string name,
                int sampleCount,
                long bytesPerOperation,
                int gen0Collections,
                double medianDurationMs)
            {
                Name = name;
                SampleCount = sampleCount;
                BytesPerOperation = bytesPerOperation;
                Gen0Collections = gen0Collections;
                MedianDurationMs = medianDurationMs;
            }

            public string Name { get; }
            public int SampleCount { get; }
            public long BytesPerOperation { get; }
            public int Gen0Collections { get; }
            public double MedianDurationMs { get; }
        }

        private readonly struct ThroughputMeasurement
        {
            public ThroughputMeasurement(
                string name,
                int operationCount,
                int errorCount,
                double requestsPerSecond,
                double p50LatencyMs,
                double p99LatencyMs)
            {
                Name = name;
                OperationCount = operationCount;
                ErrorCount = errorCount;
                RequestsPerSecond = requestsPerSecond;
                P50LatencyMs = p50LatencyMs;
                P99LatencyMs = p99LatencyMs;
            }

            public string Name { get; }
            public int OperationCount { get; }
            public int ErrorCount { get; }
            public double RequestsPerSecond { get; }
            public double P50LatencyMs { get; }
            public double P99LatencyMs { get; }
        }
    }
}
