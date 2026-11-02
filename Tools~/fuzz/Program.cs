using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace TurboHTTP.Tools.Fuzz
{
    /// <summary>
    /// Parser fuzz harness (#112). Commands:
    ///   list                                   the target names
    ///   seeds &lt;dir&gt;                           write the generated seed corpus to dir/&lt;target&gt;/
    ///   replay &lt;target&gt; &lt;path&gt;...             run each input file (directories recursively) once
    ///   mutate &lt;target&gt; --corpus &lt;dir&gt; [options] the seeded mutation loop (no coverage guidance)
    ///   libfuzzer &lt;target&gt;                     the libfuzzer-dotnet entry point (-p:FuzzDriver=libfuzzer builds);
    ///                                          with no arguments, TURBOHTTP_FUZZ_TARGET names the target
    /// Exit codes: 0 clean, 1 finding(s), 2 usage, 70 hang or memory limit (mutate/replay watchdog).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // libfuzzer-dotnet starts the target as `dotnet TurboHTTP.Fuzz.dll` (one argument only), so
            // run.sh names the target in the environment.
            string libFuzzerTarget = Environment.GetEnvironmentVariable("TURBOHTTP_FUZZ_TARGET");
            if (args.Length == 0 && !string.IsNullOrEmpty(libFuzzerTarget))
                return LibFuzzer(libFuzzerTarget);
            if (args.Length == 0)
                return Usage();

            switch (args[0])
            {
                case "list":
                    foreach (var name in Targets.All.Keys)
                        Console.WriteLine(name);
                    return 0;
                case "seeds" when args.Length == 2:
                    return WriteSeeds(args[1]);
                case "replay" when args.Length >= 3:
                    return Replay(args);
                case "mutate" when args.Length >= 2:
                    return Mutate(args);
                case "libfuzzer" when args.Length == 2:
                    return LibFuzzer(args[1]);
                default:
                    return Usage();
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("usage: TurboHTTP.Fuzz list | seeds <dir> | replay <target> [--trace] <path>... |");
            Console.Error.WriteLine("       mutate <target> --corpus <dir> [--execs N] [--seed S] [--max-len L] [--timeout-ms T] [--memory-mb M] [--artifacts <dir>] |");
            Console.Error.WriteLine("       libfuzzer <target>");
            return 2;
        }

        private static Action<byte[]> Target(string name)
        {
            if (Targets.All.TryGetValue(name, out var target))
                return target;
            Console.Error.WriteLine($"unknown target '{name}'; known: {string.Join(", ", Targets.All.Keys)}");
            Environment.Exit(2);
            return null;
        }

        private static int WriteSeeds(string root)
        {
            foreach (var pair in Seeds.Build())
            {
                Target(pair.Key);
                var dir = Path.Combine(root, pair.Key);
                Directory.CreateDirectory(dir);
                foreach (var (name, data) in pair.Value)
                    File.WriteAllBytes(Path.Combine(dir, "seed-" + name), data);
                Console.WriteLine($"{pair.Key}: {pair.Value.Count} seeds");
            }

            return 0;
        }

        private static int Replay(string[] args)
        {
            var target = Target(args[1]);
            var inputs = new List<string>();
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--trace")
                    Targets.Trace = true;
                else
                    inputs.AddRange(Files(args[i]));
            }

            using var watchdog = new Watchdog(args[1], Targets.HangMilliseconds * 3, 2048, artifacts: null);
            int findings = 0;
            foreach (var path in inputs)
            {
                var data = File.ReadAllBytes(path);
                watchdog.Begin(data);
                if (Targets.Trace)
                    Console.WriteLine(path);
                try
                {
                    target(data);
                }
                catch (Exception ex)
                {
                    findings++;
                    Console.Error.WriteLine($"FINDING {args[1]} {path}: {ex}");
                }
                finally
                {
                    watchdog.End();
                }
            }

            Console.WriteLine($"replay {args[1]}: {inputs.Count} inputs, {findings} findings");
            return findings == 0 ? 0 : 1;
        }

        private static int Mutate(string[] args)
        {
            string name = args[1];
            var target = Target(name);
            string corpusDir = null;
            string artifacts = Path.Combine(".build", "fuzz", "artifacts", name);
            long execs = 1_000_000;
            ulong seed = 1;
            int maxLen = 4096;
            int timeoutMs = Targets.HangMilliseconds * 3;
            int memoryMb = 2048;
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--corpus": corpusDir = value; break;
                    case "--artifacts": artifacts = value; break;
                    case "--execs": execs = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--seed": seed = ulong.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--max-len": maxLen = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--timeout-ms": timeoutMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--memory-mb": memoryMb = int.Parse(value, CultureInfo.InvariantCulture); break;
                    default: return Usage();
                }
            }

            if (corpusDir == null)
                return Usage();

            var corpus = new List<byte[]>();
            foreach (var path in Files(corpusDir))
                corpus.Add(File.ReadAllBytes(path));
            if (corpus.Count == 0)
            {
                Console.Error.WriteLine($"mutate {name}: empty corpus {corpusDir}");
                return 2;
            }

            Directory.CreateDirectory(artifacts);
            var mutator = new Mutator(seed);
            var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
            using var watchdog = new Watchdog(name, timeoutMs, memoryMb, artifacts);
            var clock = Stopwatch.StartNew();
            long findings = 0;
            for (long exec = 0; exec < execs; exec++)
            {
                // The seeds first, unmutated; then mutations of a random corpus entry.
                var data = exec < corpus.Count
                    ? corpus[(int)exec]
                    : mutator.Mutate(corpus[mutator.Next(corpus.Count)], corpus, maxLen);
                watchdog.Begin(data);
                try
                {
                    target(data);
                }
                catch (Exception ex)
                {
                    findings++;
                    string signature = Signature(ex);
                    signatures.TryGetValue(signature, out int seen);
                    signatures[signature] = seen + 1;
                    if (seen == 0)
                    {
                        string file = Path.Combine(artifacts, "crash-" + Hash(data));
                        File.WriteAllBytes(file, data);
                        Console.Error.WriteLine($"FINDING {name} exec {exec}: {file}\n{ex}");
                    }
                }
                finally
                {
                    watchdog.End();
                }

                if ((exec & 0xFFFF) == 0 && exec > 0)
                    Console.WriteLine($"mutate {name}: {exec} execs, {exec / Math.Max(1, clock.Elapsed.TotalSeconds):F0}/s, {findings} findings");
            }

            Console.WriteLine($"mutate {name}: {execs} execs in {clock.Elapsed.TotalSeconds:F0} s (seed {seed}, corpus {corpus.Count}, max-len {maxLen}), " +
                $"{findings} findings, {signatures.Count} unique");
            foreach (var pair in signatures)
                Console.WriteLine($"  {pair.Value,8} x {pair.Key}");
            return findings == 0 ? 0 : 1;
        }

        private static int LibFuzzer(string name)
        {
            var target = Target(name);
#if TURBOHTTP_LIBFUZZER
            // Coverage-guided: the instrumented TurboHTTP DLLs report edges to libfuzzer-dotnet. An
            // exception escaping the target is a crash; libFuzzer's -timeout and -rss_limit_mb catch
            // hangs and memory blow-ups.
            SharpFuzz.Fuzzer.LibFuzzer.Run(span => target(span.ToArray()));
            return 0;
#else
            GC.KeepAlive(target);
            Console.Error.WriteLine("libfuzzer: this build has no SharpFuzz driver; rebuild with -p:FuzzDriver=libfuzzer (Tools~/fuzz/run.sh --driver libfuzzer)");
            return 2;
#endif
        }

        private static IEnumerable<string> Files(string path)
        {
            if (File.Exists(path))
                return new[] { path };
            if (!Directory.Exists(path))
                return Array.Empty<string>();
            var files = new List<string>(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>Exception type plus the innermost TurboHTTP frame: one line per distinct defect.</summary>
        private static string Signature(Exception ex)
        {
            var root = ex;
            while (root.InnerException != null)
                root = root.InnerException;
            string frame = "?";
            foreach (var line in (root.StackTrace ?? string.Empty).Split('\n'))
            {
                if (line.Contains("TurboHTTP.") && !line.Contains("TurboHTTP.Tools.Fuzz"))
                {
                    frame = line.Trim();
                    break;
                }
            }

            return $"{ex.GetType().Name}/{root.GetType().Name} at {frame}";
        }

        private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        /// <summary>
        /// Hang and memory limit for the mutate and replay drivers (libFuzzer has its own): a background
        /// thread polls the current exec; past the limit it saves the input and ends the process.
        /// </summary>
        private sealed class Watchdog : IDisposable
        {
            private readonly string _target;
            private readonly int _timeoutMs;
            private readonly long _memoryBytes;
            private readonly string _artifacts;
            private readonly Thread _thread;
            private volatile byte[] _current;
            private long _startTicks;
            private volatile bool _stop;

            public Watchdog(string target, int timeoutMs, int memoryMb, string artifacts)
            {
                _target = target;
                _timeoutMs = timeoutMs;
                _memoryBytes = memoryMb * 1024L * 1024L;
                _artifacts = artifacts;
                _thread = new Thread(Poll) { IsBackground = true, Name = "fuzz-watchdog" };
                _thread.Start();
            }

            public void Begin(byte[] data)
            {
                Interlocked.Exchange(ref _startTicks, Stopwatch.GetTimestamp());
                _current = data;
            }

            public void End() => _current = null;

            public void Dispose()
            {
                _stop = true;
                _thread.Join();
            }

            private void Poll()
            {
                while (!_stop)
                {
                    // A polling interval, not synchronization: the watchdog only observes.
                    Thread.Sleep(200);
                    var data = _current;
                    if (data == null)
                        continue;

                    long elapsedMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _startTicks)) * 1000 / Stopwatch.Frequency;
                    if (elapsedMs > _timeoutMs)
                        Fail("timeout", $"one input ran for {elapsedMs} ms", data);
                    else if (Environment.WorkingSet > _memoryBytes)
                        Fail("oom", $"working set {Environment.WorkingSet / (1024 * 1024)} MB", data);
                }
            }

            private void Fail(string kind, string detail, byte[] data)
            {
                string file = null;
                if (_artifacts != null)
                {
                    Directory.CreateDirectory(_artifacts);
                    file = Path.Combine(_artifacts, kind + "-" + Hash(data));
                    File.WriteAllBytes(file, data);
                }

                Console.Error.WriteLine($"FINDING {_target} {kind}: {detail}; input {file ?? "(not saved)"}");
                Console.Error.Flush();
                Environment.Exit(70);
            }
        }
    }
}
