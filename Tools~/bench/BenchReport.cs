using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboHTTP.Tools.Bench
{
    internal sealed class ScenarioResult
    {
        public string Name { get; set; }
        /// <summary>A small buffered round trip: held to the regression budget by --compare.</summary>
        public bool Budgeted { get; set; }
        public int Requests { get; set; }
        /// <summary>The lowest per-round median latency (background load only adds latency).</summary>
        public double? P50Us { get; set; }
        public double? P95Us { get; set; }
        public double MeanUs { get; set; }
        /// <summary>Median over rounds of process-wide managed bytes allocated per request.</summary>
        public double? AllocBytesPerRequest { get; set; }
        public double? MbPerSecond { get; set; }
        public double? PeakHeapGrowthMb { get; set; }
        public Dictionary<string, double> Extra { get; set; }

        public string Describe()
        {
            var parts = new List<string> { Name.PadRight(30) };
            if (P50Us != null)
                parts.Add("p50 " + F(P50Us) + " us  p95 " + F(P95Us) + " us");
            else
                parts.Add("mean " + F(MeanUs / 1000.0) + " ms");
            if (AllocBytesPerRequest != null)
                parts.Add("alloc " + F(AllocBytesPerRequest) + " B/req");
            if (MbPerSecond != null)
                parts.Add(F(MbPerSecond) + " MiB/s");
            if (PeakHeapGrowthMb != null)
                parts.Add("peak heap +" + F(PeakHeapGrowthMb) + " MiB");
            if (Extra != null)
                parts.AddRange(Extra.Select(kv => kv.Key + "=" + F(kv.Value)));
            return string.Join("  ", parts);
        }

        private static string F(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
    }

    internal sealed class BenchMachine
    {
        public string Os { get; set; }
        public string Arch { get; set; }
        public string Runtime { get; set; }
        public int Processors { get; set; }

        public bool SameAs(BenchMachine other) =>
            other != null && Os == other.Os && Arch == other.Arch && Processors == other.Processors;

        public override string ToString() => Os + " " + Arch + " x" + Processors + " " + Runtime;
    }

    internal sealed class BenchReportData
    {
        public int Schema { get; set; } = 1;
        public string Note { get; set; }
        public string RecordedUtc { get; set; }
        public string Commit { get; set; }
        public BenchMachine Machine { get; set; }
        public Dictionary<string, int> Options { get; set; }
        public List<ScenarioResult> Scenarios { get; set; }
        public List<string> Comparison { get; set; }
    }

    /// <summary>
    /// result.json / baseline.json and the regression check. A budgeted scenario regresses when its p50
    /// latency exceeds the baseline by more than the budget (latency is only gated when the baseline was
    /// recorded on the same machine shape: loopback latency does not transfer between machines), or when its
    /// managed bytes per request exceed the baseline by more than the budget plus <see cref="AllocSlackBytes"/>
    /// (allocation does transfer, so it is gated everywhere).
    /// </summary>
    internal static class BenchReport
    {
        /// <summary>Absolute allowance on top of the percentage: rounding and runtime-internal jitter.</summary>
        public const double AllocSlackBytes = 64;

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static BenchReportData Create(List<ScenarioResult> results, BenchOptions options)
        {
            return new BenchReportData
            {
                Note = "TurboHTTP streaming benchmark (Tools~/bench, Phase 22a.6). Loopback Kestrel peer in a separate process; " +
                       "alloc_bytes_per_request is process-wide and includes the caller's own async state machine.",
                RecordedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Commit = Environment.GetEnvironmentVariable("BENCH_COMMIT"),
                Machine = new BenchMachine
                {
                    Os = RuntimeInformation.OSDescription,
                    Arch = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    Processors = Environment.ProcessorCount,
                },
                Options = new Dictionary<string, int>
                {
                    ["iterations"] = options.Iterations,
                    ["warmup"] = options.Warmup,
                    ["rounds"] = options.Rounds,
                },
                Scenarios = results,
            };
        }

        public static BenchReportData Load(string path) =>
            JsonSerializer.Deserialize<BenchReportData>(File.ReadAllText(path), s_json)
            ?? throw new InvalidDataException("empty baseline " + path);

        public static string Serialize(BenchReportData report) => JsonSerializer.Serialize(report, s_json) + "\n";

        /// <summary>Prints one line per scenario present in both; returns 1 when a budgeted scenario regressed.</summary>
        public static int Compare(BenchReportData current, BenchReportData baseline, double budgetPercent)
        {
            bool gateLatency = current.Machine.SameAs(baseline.Machine);
            var lines = new List<string>();
            if (!gateLatency)
            {
                lines.Add("baseline machine (" + baseline.Machine + ") differs from this one (" + current.Machine +
                          "): latency deltas are advisory, allocation is still gated");
            }

            double factor = 1 + budgetPercent / 100.0;
            bool failed = false;
            foreach (var now in current.Scenarios)
            {
                var then = baseline.Scenarios.FirstOrDefault(s => s.Name == now.Name);
                if (then == null)
                    continue;

                var notes = new List<string>();
                bool budgeted = then.Budgeted;
                if (now.P50Us != null && then.P50Us > 0)
                {
                    bool over = now.P50Us > then.P50Us * factor;
                    notes.Add("p50 " + Delta(now.P50Us.Value, then.P50Us.Value) + (over && budgeted && gateLatency ? " OVER BUDGET" : ""));
                    failed |= over && budgeted && gateLatency;
                }

                if (now.AllocBytesPerRequest != null && then.AllocBytesPerRequest != null)
                {
                    bool over = now.AllocBytesPerRequest > then.AllocBytesPerRequest * factor + AllocSlackBytes;
                    notes.Add("alloc " + then.AllocBytesPerRequest + " -> " + now.AllocBytesPerRequest + " B" + (over && budgeted ? " OVER BUDGET" : ""));
                    failed |= over && budgeted;
                }

                if (now.MbPerSecond != null && then.MbPerSecond > 0)
                    notes.Add("throughput " + Delta(now.MbPerSecond.Value, then.MbPerSecond.Value));
                if (now.PeakHeapGrowthMb != null && then.PeakHeapGrowthMb != null)
                    notes.Add("peak heap " + then.PeakHeapGrowthMb + " -> " + now.PeakHeapGrowthMb + " MiB");
                lines.Add(now.Name.PadRight(30) + (budgeted ? "[budget " + budgetPercent.ToString(CultureInfo.InvariantCulture) + "%] " : "[info] ") + string.Join(", ", notes));
            }

            lines.Add(failed ? "bench: REGRESSION over the " + budgetPercent.ToString(CultureInfo.InvariantCulture) + " % budget" : "bench: within budget");
            foreach (var line in lines)
                Console.WriteLine(line);
            current.Comparison = lines;
            return failed ? 1 : 0;
        }

        private static string Delta(double now, double then) =>
            then.ToString("0.0", CultureInfo.InvariantCulture) + " -> " + now.ToString("0.0", CultureInfo.InvariantCulture) +
            " (" + ((now - then) / then * 100).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " %)";
    }
}
