// Streaming benchmark harness (Phase 22a.6, #102). `run` measures TurboHTTP per-request latency, managed
// allocation, throughput and peak heap on HTTP/1.1 and HTTP/2, buffered and streaming, against a separate
// `serve` process (Kestrel on loopback), and optionally checks them against a committed baseline.
// Run through run.sh (README.md); `generate` writes the per-run PKI that run.sh trusts via SSL_CERT_FILE.
using System;
using System.Globalization;
using System.Threading.Tasks;
using TurboHTTP.Tools.Soak;

namespace TurboHTTP.Tools.Bench
{
    internal static class Program
    {
        private const string Usage =
            "usage: TurboHTTP.Bench generate <dir>\n" +
            "       TurboHTTP.Bench serve <dir>\n" +
            "       TurboHTTP.Bench run <dir> --out DIR [--iterations N] [--warmup N] [--rounds N] [--heavy]\n" +
            "                              [--only a,b] [--compare baseline.json] [--budget PERCENT] [--gate-latency] [--write-baseline FILE]";

        private static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            string mode = args[0];
            var options = new BenchOptions { PkiDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--out" when value != null: options.OutDir = value; i++; break;
                    case "--iterations" when value != null: options.Iterations = ParsePositive(value); i++; break;
                    case "--warmup" when value != null: options.Warmup = ParsePositive(value); i++; break;
                    case "--rounds" when value != null: options.Rounds = ParsePositive(value); i++; break;
                    case "--only" when value != null: options.Only = value.Split(','); i++; break;
                    case "--compare" when value != null: options.ComparePath = value; i++; break;
                    case "--write-baseline" when value != null: options.WriteBaselinePath = value; i++; break;
                    case "--budget" when value != null:
                        options.BudgetPercent = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture); i++; break;
                    case "--heavy": options.Heavy = true; break;
                    case "--gate-latency": options.GateLatency = true; break;
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }

            switch (mode)
            {
                case "generate":
                    SoakPki.Generate(options.PkiDir);
                    return 0;
                case "serve":
                    return await BenchServer.RunAsync(options.PkiDir).ConfigureAwait(false);
                case "run" when options.OutDir != null:
                    try
                    {
                        return await new BenchClient(options).RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine("bench: FAILED: " + e);
                        return 1;
                    }
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        private static int ParsePositive(string value)
        {
            int parsed = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (parsed <= 0)
                throw new FormatException("expected a positive integer, got " + value);
            return parsed;
        }
    }
}
