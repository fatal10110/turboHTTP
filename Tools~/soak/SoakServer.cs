using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TurboHTTP.Tools.Soak
{
    /// <summary>
    /// The `serve` process: Kestrel as the independent peer (HTTP/1.1 cleartext with WebSocket; HTTP/1.1 and
    /// HTTP/2 over TLS with ALPN) and one fault proxy per profile in front of each. It prints one READY line
    /// with the ports, answers `stats` lines on stdin with a JSON line, and exits when stdin closes.
    /// </summary>
    internal static class SoakServer
    {
        public const int MaxEchoBytes = 8 * 1024 * 1024;
        public const int MaxWebSocketMessageBytes = 1024 * 1024;

        private static int s_h1Open;
        private static int s_h2Open;
        private static long s_h1Accepted;
        private static long s_h2Accepted;

        public static async Task<int> RunAsync(string dir, int seed)
        {
            var certificate = SoakPki.LoadServerCertificate(dir);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = dir });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = MaxEchoBytes;
                // The RFC 9113 Section 6.9.2 default windows: a large upload is flow controlled throughout.
                kestrel.Limits.Http2.InitialConnectionWindowSize = 65535;
                kestrel.Limits.Http2.InitialStreamWindowSize = 65535;
                kestrel.Limits.Http2.MaxStreamsPerConnection = 100;
                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.Use(next => CountConnections(next, isTls: false));
                });
                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.Use(next => CountConnections(next, isTls: true));
                    listen.UseHttps(certificate);
                });
            });

            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
            app.Run(HandleAsync);
            await app.StartAsync().ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses;
            int h1Port = addresses.Select(a => new Uri(a)).First(u => u.Scheme == "http").Port;
            int tlsPort = addresses.Select(a => new Uri(a)).First(u => u.Scheme == "https").Port;

            var proxies = new List<(FaultProfile Profile, FaultProxy H1, FaultProxy Tls)>();
            var seeds = new Random(seed);
            foreach (var profile in FaultProfile.All())
            {
                var h1 = new FaultProxy(profile, new IPEndPoint(IPAddress.Loopback, h1Port), seeds.Next());
                var tls = new FaultProxy(profile, new IPEndPoint(IPAddress.Loopback, tlsPort), seeds.Next());
                h1.Start();
                tls.Start();
                proxies.Add((profile, h1, tls));
            }

            var ready = new Dictionary<string, object>
            {
                ["clean"] = new Dictionary<string, int> { ["h1"] = h1Port, ["tls"] = tlsPort },
            };
            foreach (var p in proxies)
                ready[p.Profile.Name] = new Dictionary<string, int> { ["h1"] = p.H1.Port, ["tls"] = p.Tls.Port };
            Console.Out.WriteLine("READY " + JsonSerializer.Serialize(ready));
            Console.Out.Flush();

            string line;
            while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Trim() != "stats")
                    continue;
                var stats = new Dictionary<string, long>
                {
                    ["h1Open"] = Volatile.Read(ref s_h1Open),
                    ["tlsOpen"] = Volatile.Read(ref s_h2Open),
                    ["h1Accepted"] = Interlocked.Read(ref s_h1Accepted),
                    ["tlsAccepted"] = Interlocked.Read(ref s_h2Accepted),
                    ["proxyOpen"] = proxies.Sum(p => p.H1.Open + p.Tls.Open),
                    ["proxyAccepted"] = proxies.Sum(p => p.H1.Accepted + p.Tls.Accepted),
                    ["proxyResets"] = proxies.Sum(p => p.H1.Resets + p.Tls.Resets),
                };
                Console.Out.WriteLine("STATS " + JsonSerializer.Serialize(stats));
                Console.Out.Flush();
            }

            foreach (var p in proxies)
            {
                p.H1.Dispose();
                p.Tls.Dispose();
            }

            await app.StopAsync().ConfigureAwait(false);
            return 0;
        }

        private static ConnectionDelegate CountConnections(ConnectionDelegate next, bool isTls)
        {
            return async context =>
            {
                if (isTls)
                {
                    Interlocked.Increment(ref s_h2Accepted);
                    Interlocked.Increment(ref s_h2Open);
                }
                else
                {
                    Interlocked.Increment(ref s_h1Accepted);
                    Interlocked.Increment(ref s_h1Open);
                }

                try
                {
                    await next(context).ConfigureAwait(false);
                }
                finally
                {
                    if (isTls)
                        Interlocked.Decrement(ref s_h2Open);
                    else
                        Interlocked.Decrement(ref s_h1Open);
                }
            };
        }

        private static async Task HandleAsync(HttpContext context)
        {
            var ct = context.RequestAborted;
            string path = context.Request.Path.Value ?? "/";
            // The client checks this on every TLS row, so an ALPN fallback to HTTP/1.1 cannot pass as HTTP/2.
            context.Response.Headers["x-soak-protocol"] = context.Request.Protocol;
            try
            {
                if (path == "/ws")
                {
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                    await EchoWebSocketAsync(socket, ct).ConfigureAwait(false);
                    return;
                }

                if (path == "/echo")
                {
                    var body = await ReadBodyAsync(context.Request.Body, ct).ConfigureAwait(false);
                    context.Response.ContentLength = body.Length;
                    await context.Response.Body.WriteAsync(body, ct).ConfigureAwait(false);
                    return;
                }

                if (TryParseTail(path, "/bytes/", out int size))
                {
                    context.Response.ContentLength = size;
                    await Pattern.WriteAsync(context.Response.Body, size, flushEachChunk: false, ct).ConfigureAwait(false);
                    return;
                }

                if (TryParseTail(path, "/chunked/", out size))
                {
                    await Pattern.WriteAsync(context.Response.Body, size, flushEachChunk: true, ct).ConfigureAwait(false);
                    return;
                }

                if (TryParseTail(path, "/slow/", out int delayMs))
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    context.Response.ContentLength = 2;
                    await context.Response.WriteAsync("ok", ct).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException ||
                                       ex is WebSocketException || ex is ConnectionResetException)
            {
                // The client cancelled, abandoned the body or the proxy reset the connection.
            }
        }

        private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxEchoBytes)
                    throw new IOException("echo body too large");
                buffer.Write(chunk, 0, n);
            }

            return buffer.ToArray();
        }

        private static async Task EchoWebSocketAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
        {
            var chunk = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                    return;
                }

                if (message.Length + result.Count > MaxWebSocketMessageBytes)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, ct).ConfigureAwait(false);
                    return;
                }

                message.Write(chunk, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                await socket.SendAsync(new ArraySegment<byte>(message.GetBuffer(), 0, (int)message.Length),
                    result.MessageType, true, ct).ConfigureAwait(false);
                message.SetLength(0);
            }
        }

        private static bool TryParseTail(string path, string prefix, out int value)
        {
            value = 0;
            return path.StartsWith(prefix, StringComparison.Ordinal) &&
                   int.TryParse(path.AsSpan(prefix.Length), out value) && value >= 0 && value <= 64 * 1024 * 1024;
        }
    }

    /// <summary>The body pattern both processes agree on: byte i is (i * 31 + 7) mod 256 (period 256).</summary>
    internal static class Pattern
    {
        public const int BlockBytes = 64 * 1024;
        public static readonly byte[] Block = Create(BlockBytes + 256);

        public static async Task WriteAsync(Stream stream, int size, bool flushEachChunk, CancellationToken ct)
        {
            int written = 0;
            while (written < size)
            {
                int n = Math.Min(flushEachChunk ? 16 * 1024 : BlockBytes, size - written);
                await stream.WriteAsync(Block.AsMemory(written % 256, n), ct).ConfigureAwait(false);
                written += n;
                if (flushEachChunk)
                    await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>True when <paramref name="data"/> is the pattern starting at absolute offset <paramref name="offset"/>.</summary>
        public static bool Matches(ReadOnlySpan<byte> data, long offset)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, BlockBytes);
                if (!data.Slice(0, n).SequenceEqual(Block.AsSpan((int)(offset % 256), n)))
                    return false;
                data = data.Slice(n);
                offset += n;
            }

            return true;
        }

        private static byte[] Create(int length)
        {
            var block = new byte[length];
            for (int i = 0; i < length; i++)
                block[i] = (byte)((i * 31 + 7) & 0xFF);
            return block;
        }
    }
}
