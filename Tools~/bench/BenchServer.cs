using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TurboHTTP.Tools.Soak;

namespace TurboHTTP.Tools.Bench
{
    /// <summary>
    /// The `serve` process: Kestrel on loopback, HTTP/1.1 cleartext on one port and HTTP/1.1 + HTTP/2 over TLS
    /// (ALPN) on another, HTTP/1.1-only TLS on a third. Routes: <c>GET /bytes/N</c> (N pattern bytes, Content-Length framed),
    /// <c>POST /echo</c> (the body back, up to <see cref="MaxEchoBytes"/>), <c>POST /sink</c> (reads and
    /// discards the body, answers its length). Every response carries <c>x-bench-protocol</c> so the client
    /// can prove an HTTP/2 row did not fall back to HTTP/1.1. Prints one READY line; exits when stdin closes.
    /// </summary>
    internal static class BenchServer
    {
        public const int MaxEchoBytes = 64 * 1024;
        public const long MaxBytesRoute = 256L * 1024 * 1024;
        private const int WriteChunk = 64 * 1024;

        public static async Task<int> RunAsync(string pkiDir)
        {
            var certificate = SoakPki.LoadServerCertificate(pkiDir);
            ListenOptions h2Listen = null;
            ListenOptions h1TlsListen = null;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = pkiDir });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
                kestrel.Limits.Http2.MaxStreamsPerConnection = 100;
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(certificate);
                    h2Listen = listen;
                });
                // HTTP/1.1-only TLS (ALPN offers no h2): separates the TLS cost from the HTTP/2 cost.
                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.UseHttps(certificate);
                    h1TlsListen = listen;
                });
            });

            var app = builder.Build();
            app.Run(HandleAsync);
            await app.StartAsync().ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses;
            int h1Port = addresses.Select(a => new Uri(a)).First(u => u.Scheme == "http").Port;
            // Kestrel writes the bound endpoint back into each ListenOptions when it binds port 0.
            int tlsPort = ((IPEndPoint)h2Listen.EndPoint).Port;
            int tls1Port = ((IPEndPoint)h1TlsListen.EndPoint).Port;
            Console.Out.WriteLine("READY " + JsonSerializer.Serialize(new { h1 = h1Port, tls = tlsPort, tls1 = tls1Port }));
            Console.Out.Flush();

            while (await Console.In.ReadLineAsync().ConfigureAwait(false) != null)
            {
            }

            await app.StopAsync().ConfigureAwait(false);
            return 0;
        }

        private static async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers["x-bench-protocol"] = request.Protocol;
            string path = request.Path.Value ?? "/";

            if (HttpMethods.IsGet(request.Method) && path.StartsWith("/bytes/", StringComparison.Ordinal) &&
                long.TryParse(path.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out long size) &&
                size <= MaxBytesRoute)
            {
                response.ContentType = "application/octet-stream";
                response.ContentLength = size;
                long sent = 0;
                while (sent < size)
                {
                    int n = (int)Math.Min(WriteChunk, size - sent);
                    await response.Body.WriteAsync(Pattern.Block.AsMemory(0, n), context.RequestAborted).ConfigureAwait(false);
                    sent += n;
                }

                return;
            }

            if (HttpMethods.IsPost(request.Method) && path == "/echo")
            {
                var buffer = ArrayPool<byte>.Shared.Rent(MaxEchoBytes);
                try
                {
                    int total = 0;
                    int n;
                    while ((n = await request.Body.ReadAsync(buffer.AsMemory(total, MaxEchoBytes - total), context.RequestAborted).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total == MaxEchoBytes)
                        {
                            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                            return;
                        }
                    }

                    response.ContentType = "application/octet-stream";
                    response.ContentLength = total;
                    await response.Body.WriteAsync(buffer.AsMemory(0, total), context.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return;
            }

            if (HttpMethods.IsPost(request.Method) && path == "/sink")
            {
                var buffer = ArrayPool<byte>.Shared.Rent(WriteChunk);
                try
                {
                    long total = 0;
                    int n;
                    while ((n = await request.Body.ReadAsync(buffer.AsMemory(0, WriteChunk), context.RequestAborted).ConfigureAwait(false)) > 0)
                        total += n;
                    await response.WriteAsync(total.ToString(CultureInfo.InvariantCulture), context.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return;
            }

            response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>The body pattern both processes agree on: byte i of every body is (i % 64K) * 31 % 251.</summary>
    internal static class Pattern
    {
        public const int BlockSize = 64 * 1024;
        public static readonly byte[] Block = Create();

        private static byte[] Create()
        {
            var block = new byte[BlockSize];
            for (int i = 0; i < block.Length; i++)
                block[i] = (byte)(i * 31 % 251);
            return block;
        }

        /// <summary>True when <paramref name="data"/> is the pattern starting at body offset <paramref name="offset"/>.</summary>
        public static bool Matches(ReadOnlySpan<byte> data, long offset)
        {
            int start = (int)(offset % BlockSize);
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, BlockSize - start);
                if (!data.Slice(0, n).SequenceEqual(Block.AsSpan(start, n)))
                    return false;
                data = data.Slice(n);
                start = 0;
            }

            return true;
        }
    }
}
