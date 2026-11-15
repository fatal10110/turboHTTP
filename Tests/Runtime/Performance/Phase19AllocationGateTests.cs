using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Testing;
using TurboHTTP.Tests.Transport;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Phase 19 allocation gate and throughput report over <see cref="MockTransport"/> and the HTTP/2 pooled
    /// completion sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the gate measures.</b> <see cref="AllocationScenarios_RespectBaselineBudget"/> counts the managed
    /// bytes each scenario allocates per operation with <see cref="AllocationMeter"/>. On the Editor's Mono that
    /// is the profiler's "GC Allocated In Frame" counter around a frame that runs only the measured operations:
    /// an upper bound (other work in that frame adds to it), so each scenario runs
    /// <see cref="RoundsPerScenario"/> rounds, each alone in its frame, and the smallest round is the reading;
    /// idle frames show what the rest of a frame costs. The gate fails when the reading exceeds
    /// <c>maxBytesPerOperation</c> plus <c>defaultThresholdPercent</c> (10%) of it. It used to compare
    /// <c>GC.GetTotalMemory(true)</c> before and after, which sees only memory still reachable after a full
    /// collection (mostly <see cref="MockTransport"/>'s captured-request queue), so a transient per-request
    /// allocation never showed (#881).
    /// </para>
    /// <para>
    /// <b>Baselines</b> (<c>Tests/Benchmarks/phase19-allocation-baselines.json</c>) are measured numbers from the
    /// reference editor: the host editor at the Unity version in <c>Tools~/ci/unity-version</c>, driven by
    /// <c>Tools~/unity/run-tests.sh</c>, which compiles the package with code optimization on. The file's
    /// <c>profile</c> names the counter profile they were recorded on
    /// (<see cref="AllocationBudget.CurrentProfile"/>); an editor that measures on another profile (code
    /// optimization off, a precise per-thread counter) skips the gate instead of judging against numbers it
    /// cannot reproduce. <see cref="CommittedBaselines_LeaveNoRoomForA4KiBPerOperationRegression"/> keeps the
    /// committed budgets tight enough that a 4 KiB per-operation allocation fails the gate.
    /// </para>
    /// <para>
    /// <b>Re-record procedure</b> (after a deliberate change moves a number, or when the reference editor
    /// changes):
    /// <list type="number">
    /// <item><description>On the reference editor run the gate in record mode at least five times, each with its
    /// own output file outside the repository:
    /// <c>TURBOHTTP_ALLOCATION_BASELINE_RECORD=1 TURBOHTTP_ALLOCATION_BASELINE_OUTPUT=&lt;file&gt;
    /// './Tools~/unity/run-tests.sh' --platform PlayMode --filter
    /// 'Phase19AllocationGateTests.AllocationScenarios_RespectBaselineBudget'</c>. A Windows <c>Unity.exe</c>
    /// started from WSL sees the variables only when they are forwarded:
    /// <c>WSLENV=TURBOHTTP_ALLOCATION_BASELINE_RECORD:TURBOHTTP_ALLOCATION_BASELINE_OUTPUT/p</c>. Record mode
    /// writes the readings and passes without judging them.</description></item>
    /// <item><description>Per scenario, copy the lowest and the highest <c>observedBytesPerOperation</c> of the
    /// runs into <c>observedMinBytesPerOperation</c> and <c>observedMaxBytesPerOperation</c>, and set
    /// <c>maxBytesPerOperation</c> to the highest reading plus <see cref="BaselineMarginBytes"/> (the margin
    /// covers rounding, the 10% threshold covers run-to-run noise). Update <c>capturedAtUtc</c>,
    /// <c>recordedRuns</c> and <c>profile</c> (the <c>profile</c> every recorded file reports).</description></item>
    /// <item><description>Run the gate again without the record variables (it must pass), and
    /// <see cref="CommittedBaselines_LeaveNoRoomForA4KiBPerOperationRegression"/>. Say in the PR which
    /// change moved the numbers.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("Benchmark")]
    public sealed class Phase19AllocationGateTests
    {
        private const int RoundsPerScenario = 5;
        private const int WarmupIterations = 32;

        /// <summary>Bytes added to the highest recorded reading to form a committed budget (re-record step 2).</summary>
        private const long BaselineMarginBytes = 16;

        /// <summary>The per-operation regression the committed budgets must catch (#881).</summary>
        private const long DetectableRegressionBytes = 4096;

        private const string ThresholdPercentEnvVar = "TURBOHTTP_ALLOCATION_REGRESSION_THRESHOLD_PERCENT";
        private const string BaselineRecordEnvVar = "TURBOHTTP_ALLOCATION_BASELINE_RECORD";
        private const string BaselineOutputEnvVar = "TURBOHTTP_ALLOCATION_BASELINE_OUTPUT";

        private static readonly Func<AllocationScenario>[] s_allocationScenarios =
        {
            CreateHttp11WarmRequestScenario,
            CreateMiddlewareFastPathScenario,
            CreateHttp2PoolSingleCompletionScenario,
            CreateHttp2PoolBurstScenario
        };

        [UnityTest]
        public IEnumerator AllocationScenarios_RespectBaselineBudget()
        {
            // The budgets are calibrated on the Editor's Mono and the baseline JSON is read from the project
            // tree, which a player does not have (#321).
            if (!Application.isEditor)
                TestRequirements.IgnoreEnvironment(
                    "The allocation budgets are Editor (Mono) baselines read from Tests/Benchmarks; a player has neither.");

            var baselinePath = ResolveBaselinePath();
            var baseline = LoadBaselineDocument(baselinePath);
            var recordMode = IsBaselineRecordMode();

            using var meter = new AllocationMeter();
            yield return meter.Calibrate();

            var profile = AllocationBudget.CurrentProfile(meter.IsPrecise).ToString();
            if (!recordMode && !string.Equals(profile, baseline.Profile, StringComparison.Ordinal))
            {
                TestRequirements.IgnoreEnvironment(
                    "The Phase 19 allocation baselines were recorded on profile '" + baseline.Profile +
                    "'; this editor measures on '" + profile + "' (counter " + meter.Name +
                    "). Re-record them as the fixture's remarks describe to gate this profile.");
            }

            var measurements = new List<ScenarioMeasurement>(s_allocationScenarios.Length);
            foreach (var factory in s_allocationScenarios)
            {
                using var scenario = factory();
                var rounds = new AllocationRounds();
                yield return RecordIdleFrames(meter, rounds);

                var completedSynchronously = true;
                for (var round = 0; round < RoundsPerScenario; round++)
                {
                    RunOperations(scenario, WarmupIterations);

                    yield return null;
                    meter.Begin();
                    completedSynchronously &= RunOperations(scenario, scenario.SampleCount);
                    meter.End();
                    yield return null;

                    rounds.AddRound(meter.Allocated, scenario.SampleCount);
                }

                // A per-thread counter sees only this thread: operations that finished on the thread pool
                // would read as free.
                if (meter.IsPrecise && !completedSynchronously)
                {
                    TestRequirements.IgnoreEnvironment(
                        "Scenario '" + scenario.Name + "' completed operations on the thread pool, which the " +
                        "per-thread counter " + meter.Name + " does not see.");
                }

                var measurement = new ScenarioMeasurement(scenario.Name, scenario.SampleCount, rounds);
                measurements.Add(measurement);
                TestContext.Progress.WriteLine(
                    "[Phase19][Allocation] " + measurement.Name +
                    " bytes/op=" + measurement.BytesPerOperation +
                    " counter=" + meter.Name +
                    " profile=" + profile +
                    " sampleCount=" + measurement.SampleCount +
                    " " + rounds.Describe());
            }

            if (recordMode)
            {
                var outputPath = ResolveBaselineOutputPath(baselinePath);
                WriteObservedBaseline(outputPath, baseline.DefaultThresholdPercent, profile, meter.Name, measurements);
                TestContext.Progress.WriteLine(
                    "[Phase19][Allocation] Recorded observed baselines to: " + outputPath);
                yield break;
            }

            var thresholdPercent = ResolveThresholdPercent(baseline.DefaultThresholdPercent);
            var failures = new List<string>();
            var unresolved = new List<string>();

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

                var allowedBytesPerOp = AllowedBytesPerOperation(scenario.MaxBytesPerOperation, thresholdPercent);
                var reading =
                    "'" + measurement.Name + "': observed=" + measurement.BytesPerOperation + " bytes/op " +
                    "(smallest of " + RoundsPerScenario + " rounds), baseline=" + scenario.MaxBytesPerOperation +
                    " bytes/op, threshold=" + thresholdPercent + "%, allowed=" + allowedBytesPerOp +
                    " bytes/op, counter=" + meter.Name + ", foreign floor=" + measurement.Rounds.ForeignFloorBytes +
                    " bytes/frame, " + measurement.Rounds.Describe();

                switch (measurement.Rounds.Judge(allowedBytesPerOp))
                {
                    case AllocationVerdict.Within:
                        break;
                    case AllocationVerdict.Unresolved:
                        unresolved.Add(reading);
                        break;
                    default:
                        failures.Add(reading);
                        break;
                }
            }

            if (failures.Count > 0)
                Assert.Fail("Allocation regression in " + string.Join("; ", failures) + ".");

            if (unresolved.Count > 0)
            {
                TestRequirements.IgnoreEnvironment(
                    "Other work in the measured frames allocates enough to hide whether these scenarios are " +
                    "within budget: " + string.Join("; ", unresolved) + ".");
            }
        }

        /// <summary>
        /// #881: the committed budgets are measured numbers with a small margin, so an operation that starts
        /// allocating 4 KiB more than the lowest recorded reading fails the gate. Round ceilings far above the
        /// real cost let such a regression pass.
        /// </summary>
        [Test]
        public void CommittedBaselines_LeaveNoRoomForA4KiBPerOperationRegression()
        {
            if (!Application.isEditor)
                TestRequirements.IgnoreEnvironment(
                    "The baseline JSON is read from Tests/Benchmarks, which a player does not have.");

            var baselinePath = ResolveBaselinePath();
            var baseline = LoadBaselineDocument(baselinePath);

            Assert.IsFalse(string.IsNullOrEmpty(baseline.Profile), "The baseline file names no recording profile.");
            Assert.GreaterOrEqual(baseline.RecordedRuns, 2, "The baselines must come from several recording runs.");
            Assert.AreEqual(s_allocationScenarios.Length, baseline.Scenarios.Count, "One baseline per scenario.");

            foreach (var scenario in baseline.Scenarios.Values)
            {
                Assert.GreaterOrEqual(
                    scenario.ObservedMinBytesPerOperation, 0, "'" + scenario.Name + "' has no recorded minimum.");
                Assert.GreaterOrEqual(
                    scenario.ObservedMaxBytesPerOperation,
                    scenario.ObservedMinBytesPerOperation,
                    "'" + scenario.Name + "' recorded spread is inverted.");
                Assert.GreaterOrEqual(
                    scenario.MaxBytesPerOperation,
                    scenario.ObservedMaxBytesPerOperation,
                    "'" + scenario.Name + "' budget is below its own recorded spread.");

                var allowed = AllowedBytesPerOperation(scenario.MaxBytesPerOperation, baseline.DefaultThresholdPercent);
                Assert.Less(
                    allowed,
                    scenario.ObservedMinBytesPerOperation + DetectableRegressionBytes,
                    "'" + scenario.Name + "' allows " + allowed + " bytes/op, so a " + DetectableRegressionBytes +
                    " B/op regression on its lowest recorded reading (" + scenario.ObservedMinBytesPerOperation +
                    ") would pass.");
            }
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

        private static AllocationScenario CreateHttp11WarmRequestScenario()
        {
            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            return new AllocationScenario(
                "http11_warm_request_mock_transport",
                384,
                async _ =>
                {
                    using var response = await client.Get("https://phase19.local/http11")
                        .SendBufferedAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Unexpected non-success in HTTP/1.1 warm request scenario.");
                },
                transport.ClearCapturedRequests,
                client);
        }

        private static AllocationScenario CreateMiddlewareFastPathScenario()
        {
            var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
            var middleware = new List<IHttpInterceptor>(8)
            {
                new HeaderTagInterceptor("X-P19-A"),
                new HeaderTagInterceptor("X-P19-B"),
                new HeaderTagInterceptor("X-P19-C"),
                new HeaderTagInterceptor("X-P19-D"),
                new HeaderTagInterceptor("X-P19-E"),
                new HeaderTagInterceptor("X-P19-F"),
                new HeaderTagInterceptor("X-P19-G"),
                new HeaderTagInterceptor("X-P19-H")
            };

            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false,
                Interceptors = middleware
            });

            return new AllocationScenario(
                "middleware_chain_sync_fast_path_mock_transport",
                320,
                async _ =>
                {
                    using var response = await client.Get("https://phase19.local/middleware")
                        .SendBufferedAsync()
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Unexpected non-success in middleware fast-path scenario.");
                },
                transport.ClearCapturedRequests,
                client);
        }

        private static AllocationScenario CreateHttp2PoolSingleCompletionScenario()
        {
            var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 256);

            return new AllocationScenario(
                "http2_poolable_source_single_completion",
                1024,
                async _ =>
                {
                    var source = pool.Rent();
                    var pending = source.CreateValueTask();
                    source.SetResult(true);
                    if (!await pending.ConfigureAwait(false))
                        throw new InvalidOperationException("Unexpected false completion from poolable source.");
                },
                null,
                null);
        }

        private static AllocationScenario CreateHttp2PoolBurstScenario()
        {
            const int burstSize = 100;

            var pool = new PoolableValueTaskSourcePool<bool>(maxSize: 256);
            var sources = new PoolableValueTaskSource<bool>[burstSize];
            var pending = new ValueTask<bool>[burstSize];

            return new AllocationScenario(
                "http2_poolable_source_multiplexed_burst_100",
                160,
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
                },
                null,
                null);
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
                        .SendBufferedAsync()
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
                                .SendBufferedAsync()
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

        /// <summary>
        /// Runs <paramref name="count"/> operations on the calling thread. The synchronization context is
        /// cleared meanwhile, so an operation that does not complete synchronously continues on the thread pool
        /// instead of waiting for this blocked thread. Returns false when any operation did not complete
        /// synchronously.
        /// </summary>
        private static bool RunOperations(AllocationScenario scenario, int count)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var completedSynchronously = true;
                for (int i = 0; i < count; i++)
                {
                    var pending = scenario.Operation(i);
                    if (pending.IsCompleted)
                    {
                        pending.GetAwaiter().GetResult();
                        continue;
                    }

                    completedSynchronously = false;
                    pending.AsTask().GetAwaiter().GetResult();
                }

                return completedSynchronously;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                scenario.AfterRun?.Invoke();
            }
        }

        /// <summary>
        /// Frames that run nothing but this coroutine: the smallest is what the rest of a frame allocates on its
        /// own. A precise counter sees only the section, so it needs none.
        /// </summary>
        private static IEnumerator RecordIdleFrames(AllocationMeter meter, AllocationRounds rounds)
        {
            if (meter.IsPrecise)
                yield break;

            yield return null;
            for (var i = 0; i < RoundsPerScenario; i++)
            {
                yield return null;
                rounds.AddIdleFrame(meter.Allocated);
            }
        }

        private static long AllowedBytesPerOperation(long baselineBytesPerOperation, int thresholdPercent)
        {
            return (long)Math.Ceiling(baselineBytesPerOperation * (1.0 + (thresholdPercent / 100.0)));
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
                Profile = root.TryGetValue("profile", out var profile) ? profile as string : null,
                RecordedRuns = ReadInt(root, "recordedRuns", 0),
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
                    MinSamples = ReadInt(scenarioMap, "minSamples", 1),
                    ObservedMinBytesPerOperation = ReadInt(scenarioMap, "observedMinBytesPerOperation", -1),
                    ObservedMaxBytesPerOperation = ReadInt(scenarioMap, "observedMaxBytesPerOperation", -1)
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
            string profile,
            string counter,
            IReadOnlyList<ScenarioMeasurement> measurements)
        {
            var scenarioList = new List<object>(measurements.Count);
            for (int i = 0; i < measurements.Count; i++)
            {
                var measurement = measurements[i];
                scenarioList.Add(new Dictionary<string, object>
                {
                    { "name", measurement.Name },
                    { "observedBytesPerOperation", measurement.BytesPerOperation },
                    { "minSamples", measurement.SampleCount },
                    { "foreignFloorBytesPerFrame", measurement.Rounds.ForeignFloorBytes },
                    { "rounds", measurement.Rounds.Describe() }
                });
            }

            var root = new Dictionary<string, object>
            {
                { "version", 1 },
                { "capturedAtUtc", DateTime.UtcNow.ToString("o") },
                { "defaultThresholdPercent", defaultThresholdPercent },
                { "profile", profile },
                { "counter", counter },
                { "notes", "Recorded via " + BaselineRecordEnvVar + "=1; merge several runs as the fixture's remarks describe." },
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

        private sealed class HeaderTagInterceptor : IHttpInterceptor
        {
            private readonly string _headerName;

            public HeaderTagInterceptor(string headerName)
            {
                _headerName = headerName;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, ct) =>
                {
                    var requestForNext = request.Clone();
                    requestForNext.WithHeader(_headerName, "1");
                    context.UpdateRequest(requestForNext);
                    return next(requestForNext, handler, context, ct);
                };
            }
        }

        private sealed class BaselineDocument
        {
            public int DefaultThresholdPercent;
            public string Profile;
            public int RecordedRuns;
            public Dictionary<string, BaselineScenario> Scenarios;
        }

        private sealed class BaselineScenario
        {
            public string Name;
            public long MaxBytesPerOperation;
            public int MinSamples;
            public long ObservedMinBytesPerOperation;
            public long ObservedMaxBytesPerOperation;
        }

        /// <summary>One gate scenario: an operation and the objects it keeps alive across rounds.</summary>
        private sealed class AllocationScenario : IDisposable
        {
            private readonly IDisposable _owned;

            public AllocationScenario(
                string name,
                int sampleCount,
                Func<int, ValueTask> operation,
                Action afterRun,
                IDisposable owned)
            {
                Name = name;
                SampleCount = sampleCount;
                Operation = operation;
                AfterRun = afterRun;
                _owned = owned;
            }

            public string Name { get; }
            public int SampleCount { get; }
            public Func<int, ValueTask> Operation { get; }

            /// <summary>Runs after every batch of operations, e.g. to drop the transport's captured requests.</summary>
            public Action AfterRun { get; }

            public void Dispose()
            {
                _owned?.Dispose();
            }
        }

        private readonly struct ScenarioMeasurement
        {
            public ScenarioMeasurement(string name, int sampleCount, AllocationRounds rounds)
            {
                Name = name;
                SampleCount = sampleCount;
                Rounds = rounds;
            }

            public string Name { get; }
            public int SampleCount { get; }
            public AllocationRounds Rounds { get; }

            /// <summary>Bytes per operation of the smallest round.</summary>
            public long BytesPerOperation => Rounds.BytesPerUnit;
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
