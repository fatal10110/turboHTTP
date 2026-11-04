// Soak and lossy-network harness (#114). `run` drives TurboHTTP (HTTP/1.1, HTTP/2, streaming, WebSocket,
// cancellations) against a separate `serve` process (Kestrel + fault proxies) for a fixed duration and
// fails on unexpected errors, hangs, managed-heap growth, outstanding pooled objects or leaked sockets.
// Run through run.sh (README.md); `generate` writes the per-run PKI that run.sh trusts via SSL_CERT_FILE.
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace TurboHTTP.Tools.Soak
{
    internal static class Program
    {
        private const string Usage =
            "usage: TurboHTTP.Soak generate <dir>\n" +
            "       TurboHTTP.Soak serve <dir> [--seed N]\n" +
            "       TurboHTTP.Soak run <dir> [--duration 3600|90s|30m|1h] [--workers N] [--seed N] [--warmup S]\n" +
            "                              [--sample S] [--checkpoint S] [--max-growth-mb X] [--max-sockets N]\n" +
            "                              [--profiles clean,latency,bandwidth,lossy,reset] [--out DIR]";

        private static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            string mode = args[0];
            string dir = args[1];
            var options = new SoakOptions { PkiDir = dir };
            for (int i = 2; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--duration" when value != null: options.DurationSeconds = ParseSeconds(value); i++; break;
                    case "--workers" when value != null: options.Workers = ParseInt(value); i++; break;
                    case "--seed" when value != null: options.Seed = ParseInt(value); i++; break;
                    case "--warmup" when value != null: options.WarmupSeconds = ParseSeconds(value); i++; break;
                    case "--sample" when value != null: options.SampleSeconds = ParseSeconds(value); i++; break;
                    case "--checkpoint" when value != null: options.CheckpointSeconds = ParseSeconds(value); i++; break;
                    case "--max-growth-mb" when value != null:
                        options.MaxGrowthMb = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                    case "--max-sockets" when value != null: options.MaxSockets = ParseInt(value); i++; break;
                    case "--profiles" when value != null: options.Profiles = value.Split(','); i++; break;
                    case "--out" when value != null: options.OutDir = value; i++; break;
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }

            switch (mode)
            {
                case "generate":
                    SoakPki.Generate(dir);
                    return 0;
                case "serve":
                    return await SoakServer.RunAsync(dir, options.Seed).ConfigureAwait(false);
                case "run":
                    if (options.Workers < 1 || options.DurationSeconds < 10 || options.SampleSeconds < 1)
                    {
                        Console.Error.WriteLine("run: --workers >= 1, --duration >= 10 s and --sample >= 1 s are required");
                        return 2;
                    }

                    return await new SoakClient(options).RunAsync().ConfigureAwait(false);
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

        private static int ParseSeconds(string value)
        {
            int scale = 1;
            if (value.EndsWith("h", StringComparison.Ordinal))
                scale = 3600;
            else if (value.EndsWith("m", StringComparison.Ordinal))
                scale = 60;
            string digits = value.TrimEnd('h', 'm', 's');
            return checked(int.Parse(digits, CultureInfo.InvariantCulture) * scale);
        }
    }

    internal sealed class SoakOptions
    {
        public string PkiDir;
        public int DurationSeconds = 3600;
        public int Workers = 8;
        public int Seed = 114;
        /// <summary>-1: min(60 s, duration / 5).</summary>
        public int WarmupSeconds = -1;
        public int SampleSeconds = 10;
        public int CheckpointSeconds = 600;
        public double MaxGrowthMb = 10;
        public int MaxSockets = 256;
        public string[] Profiles = { "clean", "latency", "bandwidth", "lossy", "reset" };
        public string OutDir = ".build/soak/run";
    }
}
