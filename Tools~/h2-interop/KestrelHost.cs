using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using TurboHTTP.Tools.TlsInterop;

namespace TurboHTTP.Tools.H2Interop
{
    /// <summary>
    /// Kestrel (ASP.NET Core 8) as an in-process TLS server on loopback: HTTP/2 only for the interop rows; HTTP/2 and
    /// HTTP/1.1 for <c>serve</c> (#885), so a client that falls back to HTTP/1.1 is still served and only the Unity rows'
    /// protocol assertion catches it.
    /// </summary>
    internal sealed class KestrelHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private KestrelHost(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public int Port { get; }

        public static async Task<KestrelHost> StartAsync(string dir, int port = 0, bool allowHttp1 = false)
        {
            using var pem = X509Certificate2.CreateFromPemFile(TestPki.Leaf(dir, "valid"), Path.Combine(dir, TestPki.KeyFile));
            var certificate = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Limits.Http2.MaxStreamsPerConnection = 100;
                kestrel.Listen(IPAddress.Loopback, port, listen =>
                {
                    listen.Protocols = allowHttp1 ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http2;
                    listen.UseHttps(certificate);
                });
            });

            var app = builder.Build();
            var www = Path.Combine(dir, InteropRows.WwwDir);
            var small = File.ReadAllBytes(Path.Combine(www, "small.txt"));
            var large = File.ReadAllBytes(Path.Combine(www, "large.bin"));
            app.MapGet("/small.txt", context => context.Response.Body.WriteAsync(small, 0, small.Length));
            app.MapGet("/large.bin", context => context.Response.Body.WriteAsync(large, 0, large.Length));
            app.MapPost("/upload", async context =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(body.GetBuffer(), 0, (int)body.Length).ConfigureAwait(false);
            });
            app.MapGet("/headers", context =>
            {
                for (int i = 0; i < InteropRows.ResponseHeaderCount; i++)
                    context.Response.Headers["x-h-" + i] = InteropRows.ResponseHeaderValue(i);
                return context.Response.WriteAsync("headers\n");
            });

            await app.StartAsync().ConfigureAwait(false);
            return new KestrelHost(app, new Uri(app.Urls.First()).Port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
