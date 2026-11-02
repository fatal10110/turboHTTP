// Autobahn testsuite client harness (#217): drives TurboHTTP's WebSocketClient against the
// `wstest -m fuzzingserver` reference server. For every case the server serves, the harness opens
// /runCase, echoes each message back with the same type until the server closes, and finally asks the
// server to write its report (/updateReports). Pass/fail is decided from that report by
// check_report.py, not here. Run through run.sh (README.md).
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tools.Autobahn
{
    internal static class Program
    {
        // Case 9.1.6/9.2.6 send 16 MiB messages; 9.3.1/9.4.1 send 4 MiB in 64-byte fragments (65536).
        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private const int MaxFragments = 70000;

        private static async Task<int> Main(string[] args)
        {
            string server = "ws://127.0.0.1:9001";
            string agent = "TurboHTTP";
            // Above the fuzzing server's own per-case limit (480 s for the 128 KiB 12.x/13.x cases).
            int caseTimeoutSeconds = 600;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server" when i + 1 < args.Length: server = args[++i].TrimEnd('/'); break;
                    case "--agent" when i + 1 < args.Length: agent = args[++i]; break;
                    case "--case-timeout" when i + 1 < args.Length:
                        caseTimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    default:
                        Console.Error.WriteLine("usage: TurboHTTP.Autobahn [--server ws://host:9001] [--agent name] [--case-timeout s]");
                        return 2;
                }
            }

            // No Unity bootstrap runs in a plain .NET process, so the raw-socket transport registers here.
            RawSocketWebSocketTransport.EnsureRegistered();

            int count = await GetCaseCountAsync(new Uri(server + "/getCaseCount")).ConfigureAwait(false);
            Console.WriteLine("autobahn: " + count + " case(s) on " + server + ", agent " + agent);

            string agentArg = Uri.EscapeDataString(agent);
            int timedOut = 0;
            for (int n = 1; n <= count; n++)
            {
                var uri = new Uri(server + "/runCase?case=" + n.ToString(CultureInfo.InvariantCulture) + "&agent=" + agentArg);
                var watch = Stopwatch.StartNew();
                string outcome = await RunCaseAsync(uri, TimeSpan.FromSeconds(caseTimeoutSeconds)).ConfigureAwait(false);
                if (outcome == "timeout")
                    timedOut++;
                Console.WriteLine("autobahn: case " + n + "/" + count + " " + outcome + " (" + watch.ElapsedMilliseconds + " ms)");
            }

            await RunControlSessionAsync(new Uri(server + "/updateReports?agent=" + agentArg)).ConfigureAwait(false);
            Console.WriteLine("autobahn: report updated" + (timedOut > 0 ? "; " + timedOut + " case(s) hit the harness timeout" : ""));
            return 0;
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            var options = new WebSocketConnectionOptions
            {
                AllowCleartext = true, // the fuzzing server is ws:// on loopback
                MaxMessageSize = MaxMessageBytes,
                MaxFrameSize = MaxMessageBytes,
                MaxFragmentCount = MaxFragments,
                ReceiveQueueMaxBytes = 2 * MaxMessageBytes,
                PingInterval = TimeSpan.Zero, // no unsolicited pings inside a case's frame sequence
                HandshakeTimeout = TimeSpan.FromSeconds(60), // the server may still be finishing the last case
            };

            // After MaxMessageSize: the deflate extension captures the decompressed-size bound here.
            return options.WithCompression();
        }

        private static async Task<string> RunCaseAsync(Uri uri, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = new WebSocketClient();
            try
            {
                await client.ConnectAsync(uri, CreateOptions(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "timeout";
            }
            catch (Exception ex)
            {
                // A handshake the client refuses (e.g. a declined permessage-deflate parameter) ends here;
                // the server records the case from what it saw on the wire.
                return "handshake-failed: " + Describe(ex);
            }

            int echoed = 0;
            while (true)
            {
                WebSocketMessage message;
                try
                {
                    message = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return "timeout after " + echoed + " message(s)";
                }
                catch (WebSocketException ex)
                {
                    return "closed after " + echoed + " message(s): " + Describe(ex);
                }

                using (message)
                {
                    try
                    {
                        if (message.IsText)
                            await client.SendAsync(message.Text, cts.Token).ConfigureAwait(false);
                        else
                            await client.SendAsync(message.Data, cts.Token).ConfigureAwait(false);
                        echoed++;
                    }
                    catch (OperationCanceledException)
                    {
                        return "timeout after " + echoed + " message(s)";
                    }
                    catch (Exception ex) when (ex is WebSocketException || ex is InvalidOperationException)
                    {
                        // The peer closed between receive and echo (close frames follow data in some cases).
                        return "closed during echo after " + echoed + " message(s): " + Describe(ex);
                    }
                }
            }
        }

        private static async Task<int> GetCaseCountAsync(Uri uri)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new WebSocketClient();
            await client.ConnectAsync(uri, CreateOptions(), cts.Token).ConfigureAwait(false);
            using var message = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return int.Parse(message.Text, CultureInfo.InvariantCulture);
        }

        private static async Task RunControlSessionAsync(Uri uri)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = new WebSocketClient();
            await client.ConnectAsync(uri, CreateOptions(), cts.Token).ConfigureAwait(false);
            try
            {
                // The server closes once the report is written; drain until then.
                while (true)
                {
                    using var message = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is WebSocketException ws)
                return ws.Error + (ws.CloseCode.HasValue ? " close=" + (int)ws.CloseCode.Value : "");
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
