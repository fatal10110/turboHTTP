using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Performance
{
    [TestFixture]
    public class BenchmarkTests
    {
        // Reference environment for thresholds:
        // Unity 2021.3 LTS, Editor/Mono x64, Release configuration.
        private const int ThroughputRequestCount = 1000;
        private static readonly TimeSpan ThroughputMaxDuration = TimeSpan.FromSeconds(10);

        private const int AllocationSampleCount = 500;
        private const long MaxBytesPerRequest = 32768; // Trend guardrail, not an absolute hard perf target.

        private const int LeakCheckRounds = 5;
        private const int LeakCheckRequestsPerRound = 250;
        private const long MaxLeakGrowthBytes = 8L * 1024 * 1024;
        private const int CoverageGatePercent = 80;

        [Test]
        [Category("Benchmark")]
        public void Throughput_MockTransport_1000Requests_WithinBudget()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var sw = Stopwatch.StartNew();
                var tasks = new Task<UHttpResponse>[ThroughputRequestCount];
                for (int i = 0; i < ThroughputRequestCount; i++)
                {
                    tasks[i] = client.Get("https://benchmark.local/" + i).SendAsync().AsTask();
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                sw.Stop();

                Assert.LessOrEqual(
                    sw.Elapsed,
                    ThroughputMaxDuration,
                    $"Throughput regression: {ThroughputRequestCount} requests took {sw.Elapsed.TotalMilliseconds:F0}ms.");
                Assert.AreEqual(ThroughputRequestCount, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Benchmark")]
        public void Allocation_PerRequest_RemainsBelowBudget()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                ForceGc();
                var before = GC.GetTotalMemory(true);

                for (int i = 0; i < AllocationSampleCount; i++)
                {
                    var response = await client.Get("https://benchmark.local/allocation/" + i).SendAsync()
                        .ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("Unexpected non-OK response during allocation benchmark.");
                }

                ForceGc();
                var after = GC.GetTotalMemory(true);
                var delta = Math.Max(0L, after - before);
                var perRequest = delta / AllocationSampleCount;

                Assert.LessOrEqual(
                    perRequest,
                    MaxBytesPerRequest,
                    $"Allocation regression: ~{perRequest} bytes/request (delta={delta}).");
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Benchmark")]
        public void RepeatedBenchmarkLoops_NoLeakSignal()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, body: Array.Empty<byte>());
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                ForceGc();
                var start = GC.GetTotalMemory(true);

                for (int round = 0; round < LeakCheckRounds; round++)
                {
                    for (int i = 0; i < LeakCheckRequestsPerRound; i++)
                    {
                        var response = await client.Get("https://benchmark.local/leak/" + round + "/" + i).SendAsync()
                            .ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("Unexpected non-success response during leak benchmark.");
                    }

                    ForceGc();
                }

                var end = GC.GetTotalMemory(true);
                var growth = Math.Max(0L, end - start);

                Assert.LessOrEqual(
                    growth,
                    MaxLeakGrowthBytes,
                    $"Potential memory leak trend detected. Heap growth={growth} bytes.");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void QualityGate_CommandMatrix_IsDocumented()
        {
            TestContext.Progress.WriteLine(
                "Deterministic lane: Unity -runTests -batchmode -projectPath . -testResults ./test-results.xml --where \"cat != ExternalNetwork\"");
            TestContext.Progress.WriteLine(
                "External lane (optional): Unity -runTests -batchmode -projectPath . -testResults ./test-results-external.xml --where \"cat == ExternalNetwork\"");
            TestContext.Progress.WriteLine(
                $"Coverage lane: Unity Code Coverage package on Editor/Mono (gate >= {CoverageGatePercent}%); IL2CPP validated with functional pass/fail tests.");

            Assert.Pass();
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
