using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tools.Soak
{
    /// <summary>
    /// The operation mix. Every operation verifies what it received (status, protocol, exact body bytes)
    /// and returns its outcome: ok, cancelled (an intended cancellation) or abandoned (an intended early
    /// dispose). Anything else is thrown and classified by <see cref="SoakClient"/>.
    /// </summary>
    internal static class Ops
    {
        private static readonly (string Name, int Weight)[] Mix =
        {
            ("h1-get", 10), ("h1-echo", 6), ("h1-stream", 5),
            ("h2-get", 10), ("h2-get-large", 5), ("h2-echo", 6), ("h2-stream", 5), ("h2-burst", 3),
            ("cancel-early", 5), ("cancel-mid-body", 5),
            ("ws-echo", 5), ("ws-abort", 2),
        };

        private static readonly int TotalWeight = Sum();

        public static string Pick(Random random)
        {
            int roll = random.Next(TotalWeight);
            foreach (var (name, weight) in Mix)
            {
                if (roll < weight)
                    return name;
                roll -= weight;
            }

            return Mix[0].Name;
        }

        public static bool Cancels(string op) => op == "cancel-early" || op == "cancel-mid-body";

        public static bool IsWebSocket(string op) => op.StartsWith("ws-", StringComparison.Ordinal);

        public static bool InjectsFailures(string profile)
        {
            foreach (var p in FaultProfile.All())
            {
                if (p.Name == profile)
                    return p.InjectsFailures;
            }

            return false;
        }

        public static Task<string> RunAsync(string op, UHttpClient client, Ports ports, Random random,
            CancellationTokenSource cancel, CancellationToken watchdog)
        {
            string h1 = "http://127.0.0.1:" + ports.H1;
            string h2 = "https://127.0.0.1:" + ports.Tls;
            string any = random.Next(2) == 0 ? h1 : h2;
            switch (op)
            {
                case "h1-get": return GetAsync(client, h1, Between(random, 1, 64 * 1024), cancel.Token);
                case "h1-echo": return EchoAsync(client, h1, Between(random, 1, 256 * 1024), random, cancel.Token);
                case "h1-stream": return StreamAsync(client, h1 + "/chunked/", Between(random, 64 * 1024, 1024 * 1024), cancel.Token);
                case "h2-get": return GetAsync(client, h2, Between(random, 1, 64 * 1024), cancel.Token);
                case "h2-get-large": return GetAsync(client, h2, Between(random, 512 * 1024, 3 * 1024 * 1024), cancel.Token);
                case "h2-echo": return EchoAsync(client, h2, Between(random, 16 * 1024, 1024 * 1024), random, cancel.Token);
                case "h2-stream": return StreamAsync(client, h2 + "/chunked/", Between(random, 256 * 1024, 2 * 1024 * 1024), cancel.Token);
                case "h2-burst": return BurstAsync(client, h2, random, cancel.Token);
                case "cancel-early": return CancelEarlyAsync(client, any, random, cancel);
                case "cancel-mid-body": return CancelMidBodyAsync(client, any, random, cancel);
                case "ws-echo": return WebSocketAsync("ws://127.0.0.1:" + ports.H1 + "/ws", random, close: true, cancel.Token);
                case "ws-abort": return WebSocketAsync("ws://127.0.0.1:" + ports.H1 + "/ws", random, close: false, cancel.Token);
                default: throw new ArgumentOutOfRangeException(nameof(op), op, null);
            }
        }

        private static async Task<string> GetAsync(UHttpClient client, string origin, int size, CancellationToken ct)
        {
            using var response = await client.SendBufferedAsync(client.Get(origin + "/bytes/" + size), ct).ConfigureAwait(false);
            Check(response.StatusCode, response.Headers, origin);
            VerifyPattern(response.Body, size);
            return "ok";
        }

        private static async Task<string> EchoAsync(UHttpClient client, string origin, int size, Random random, CancellationToken ct)
        {
            var body = new byte[size];
            random.NextBytes(body);
            using var response = await client.SendBufferedAsync(client.Post(origin + "/echo").WithBody(body), ct).ConfigureAwait(false);
            Check(response.StatusCode, response.Headers, origin);
            if (response.Body.Length != size || !response.Body.ToArray().AsSpan().SequenceEqual(body))
                throw new VerificationException("echo body differs (" + response.Body.Length + " of " + size + " bytes back)");
            return "ok";
        }

        private static async Task<string> StreamAsync(UHttpClient client, string prefix, int size, CancellationToken ct)
        {
            using var response = await client.SendStreamingAsync(client.Get(prefix + size), ct).ConfigureAwait(false);
            Check(response.StatusCode, response.Headers, prefix);
            long total = await ReadVerifiedAsync(response, long.MaxValue, ct).ConfigureAwait(false);
            if (total != size)
                throw new VerificationException("streamed " + total + " of " + size + " bytes");
            return "ok";
        }

        /// <summary>16 concurrent requests: on the HTTP/2 rows they multiplex on one connection.</summary>
        private static async Task<string> BurstAsync(UHttpClient client, string origin, Random random, CancellationToken ct)
        {
            var tasks = new Task<string>[16];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = GetAsync(client, origin, Between(random, 1, 32 * 1024), ct);
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Rethrow the first failure; WhenAll already observed every task.
                foreach (var task in tasks)
                {
                    if (task.IsFaulted)
                        throw task.Exception.GetBaseException();
                }

                throw;
            }

            return "ok";
        }

        private static async Task<string> CancelEarlyAsync(UHttpClient client, string origin, Random random, CancellationTokenSource cancel)
        {
            cancel.CancelAfter(Between(random, 1, 300));
            using var response = await client.SendBufferedAsync(client.Get(origin + "/slow/2000"), cancel.Token).ConfigureAwait(false);
            Check(response.StatusCode, response.Headers, origin);
            return "ok";
        }

        private static async Task<string> CancelMidBodyAsync(UHttpClient client, string origin, Random random, CancellationTokenSource cancel)
        {
            int size = Between(random, 1024 * 1024, 4 * 1024 * 1024);
            bool abandon = random.Next(2) == 0;
            int readFirst = Between(random, 1, 256 * 1024);
            using (var response = await client.SendStreamingAsync(client.Get(origin + "/bytes/" + size), cancel.Token).ConfigureAwait(false))
            {
                Check(response.StatusCode, response.Headers, origin);
                long read = await ReadVerifiedAsync(response, readFirst, cancel.Token).ConfigureAwait(false);
                if (abandon)
                    return "abandoned"; // dispose with the rest of the body unread

                cancel.Cancel();
                read += await ReadVerifiedAsync(response, long.MaxValue, cancel.Token, read).ConfigureAwait(false);
                if (read != size)
                    throw new VerificationException("body ended at " + read + " of " + size + " bytes after cancel");
            }

            return "ok"; // the rest was already buffered when the token fired
        }

        private static async Task<string> WebSocketAsync(string url, Random random, bool close, CancellationToken ct)
        {
            using var socket = new WebSocketClient();
            await socket.ConnectAsync(new Uri(url), new WebSocketConnectionOptions { AllowCleartext = true }, ct).ConfigureAwait(false);
            int messages = close ? Between(random, 1, 10) : 1;
            for (int i = 0; i < messages; i++)
            {
                int size = Between(random, 1, 32 * 1024);
                if (random.Next(2) == 0)
                {
                    var text = new string((char)('a' + random.Next(26)), size);
                    await socket.SendAsync(text, ct).ConfigureAwait(false);
                    using var echo = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                    if (!echo.IsText || echo.Text != text)
                        throw new VerificationException("WebSocket text echo differs");
                }
                else
                {
                    var data = new byte[size];
                    random.NextBytes(data);
                    await socket.SendAsync(data, ct).ConfigureAwait(false);
                    using var echo = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                    if (!echo.IsBinary || !echo.Data.Span.SequenceEqual(data))
                        throw new VerificationException("WebSocket binary echo differs");
                }
            }

            if (!close)
                return "abandoned"; // dispose without a close handshake

            await socket.CloseAsync(WebSocketCloseCode.NormalClosure, null, ct).ConfigureAwait(false);
            return "ok";
        }

        private static async Task<long> ReadVerifiedAsync(UHttpStreamingResponse response, long limit, CancellationToken ct, long offset = 0)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                long total = 0;
                while (total < limit)
                {
                    int want = (int)Math.Min(buffer.Length, limit - total);
                    int n = await response.Body.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    if (!Pattern.Matches(buffer.AsSpan(0, n), offset + total))
                        throw new VerificationException("streamed body corrupted near byte " + (offset + total));
                    total += n;
                }

                return total;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void VerifyPattern(ReadOnlySequence<byte> body, int size)
        {
            if (body.Length != size)
                throw new VerificationException("body has " + body.Length + " of " + size + " bytes");
            long offset = 0;
            foreach (var segment in body)
            {
                if (!Pattern.Matches(segment.Span, offset))
                    throw new VerificationException("body corrupted near byte " + offset);
                offset += segment.Length;
            }
        }

        private static void Check(HttpStatusCode status, HttpHeaders headers, string origin)
        {
            if (status != HttpStatusCode.OK)
                throw new VerificationException("status " + (int)status);
            string expected = origin.StartsWith("https:", StringComparison.Ordinal) ? "HTTP/2" : "HTTP/1.1";
            string protocol = headers.Get("x-soak-protocol");
            if (protocol != expected)
                throw new VerificationException("served over " + (protocol ?? "?") + ", expected " + expected);
        }

        private static int Between(Random random, int min, int max) => random.Next(min, max + 1);

        private static int Sum()
        {
            int total = 0;
            foreach (var (_, weight) in Mix)
                total += weight;
            return total;
        }
    }
}
