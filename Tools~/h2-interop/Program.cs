// HTTP/2 adversarial-server and interop job (#111).
//
//   adversarial [--row <filter>]                       the scripted adversarial-server scenarios (in memory)
//   generate <dir>                                     writes the throwaway PKI and the served files into <dir>
//   interop <dir> <server> <port> [--row <filter>]     the interop rows against a running server
//                                                      (nghttpd, nginx, h2o, go, envoy, nghttpx: docker, started
//                                                      by run.sh; port 0 for kestrel, which the harness hosts itself)
//
// Interop needs SSL_CERT_FILE=<dir>/root.pem (run.sh sets it) so the platform verifier trusts the per-run root.
// Exit 0 when every row passes, 1 when a row or a precondition fails, 2 on usage errors. Nothing skips: a row a server
// cannot serve prints N/A, a row on a filed defect prints KNOWN-FAIL with the issue.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Tests.Transport.Http2.Adversarial;

namespace TurboHTTP.Tools.H2Interop
{
    internal static class Program
    {
        private static readonly TimeSpan RowWatchdog = TimeSpan.FromSeconds(60);

        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length >= 1 && args[0] == "adversarial" && (args.Length == 1 || (args.Length == 3 && args[1] == "--row")))
                    return await RunAdversarialAsync(args.Length == 3 ? args[2] : null).ConfigureAwait(false);

                if (args.Length == 2 && args[0] == "generate")
                {
                    InteropRows.Generate(Path.GetFullPath(args[1]));
                    return 0;
                }

                if (args.Length >= 4 && args[0] == "interop" && (args.Length == 4 || (args.Length == 6 && args[4] == "--row")))
                {
                    int port = int.Parse(args[3], CultureInfo.InvariantCulture);
                    return await InteropRows.RunAsync(Path.GetFullPath(args[1]), args[2], port, args.Length == 6 ? args[5] : null)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            {
            }

            Console.Error.WriteLine(
                "usage: TurboHTTP.H2Interop adversarial [--row <filter>] | generate <dir> | interop <dir> <server> <port> [--row <filter>]");
            return 2;
        }

        private static async Task<int> RunAdversarialAsync(string filter)
        {
            var rows = new List<(string, Func<CancellationToken, Task>)>();
            foreach (var (name, run) in Http2AdversarialScenarios.All)
            {
                if (filter == null || name.Contains(filter, StringComparison.Ordinal))
                    rows.Add(("adversarial/" + name, run));
            }

            return await RunRowsAsync(rows).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs each row under a watchdog token; a row that ignores the token still fails after twice the watchdog.
        /// A row in <paramref name="knownFailures"/> (row name to issue number) that fails prints KNOWN-FAIL and does not
        /// fail the run; one that passes fails the run, so a fixed defect cannot hide behind a stale entry.
        /// </summary>
        internal static async Task<int> RunRowsAsync(
            IReadOnlyList<(string Name, Func<CancellationToken, Task> Run)> rows,
            IReadOnlyDictionary<string, int> knownFailures = null)
        {
            if (rows.Count == 0)
            {
                Console.WriteLine("FAIL no row matches the filter");
                return 1;
            }

            int passed = 0, failed = 0, known = 0;
            foreach (var (name, run) in rows)
            {
                int issue = 0;
                bool isKnown = knownFailures != null && knownFailures.TryGetValue(name, out issue);
                using var watchdog = new CancellationTokenSource(RowWatchdog);
                var task = Task.Run(() => run(watchdog.Token));
                try
                {
                    if (await Task.WhenAny(task, Task.Delay(RowWatchdog + RowWatchdog)).ConfigureAwait(false) != task)
                        throw new TimeoutException("the row ignored its watchdog token");
                    await task.ConfigureAwait(false);
                    if (isKnown)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + name + ": passed, but is listed as a known failure (#" + issue + "); remove the entry");
                    }
                    else
                    {
                        passed++;
                        Console.WriteLine("PASS " + name);
                    }
                }
                catch (Exception ex) when (isKnown)
                {
                    known++;
                    Console.WriteLine("KNOWN-FAIL " + name + " (#" + issue + "): " + Describe(ex));
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + name + ": " + Describe(ex));
                }
            }

            Console.WriteLine(passed + "/" + rows.Count + " rows passed" + (known > 0 ? ", " + known + " known failure(s)" : ""));
            return failed == 0 ? 0 : 1;
        }

        private static string Describe(Exception ex)
        {
            var text = ex.GetType().Name + ": " + ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                text += " <- " + inner.GetType().Name + ": " + inner.Message;
            return text;
        }
    }
}
