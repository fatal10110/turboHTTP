using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Tools.TlsInterop;
using TurboHTTP.Transport;
using TurboHTTP.Transport.BouncyCastle;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tools.H2Interop
{
    /// <summary>
    /// Interop rows: the production client (<see cref="RawSocketTransport"/>, SslStream, ALPN h2) against real
    /// HTTP/2 servers over TLS on loopback. Every row checks the request went over an HTTP/2 connection.
    /// The <c>bc-*</c> rows use <see cref="TlsBackend.BouncyCastle"/> instead, directly and through a real
    /// CONNECT tunnel (<see cref="ConnectProxy"/>, #255): BouncyCastle offers ALPN h2 inside the tunnel and the
    /// HTTP/2 connection is keyed by origin and proxy. A missing BouncyCastle provider fails the run (#140).
    /// UHttpClient is not used: the editor shadow build of TurboHTTP.Core calls into the Unity engine while
    /// building its interceptors, which a plain .NET process cannot (as in Tools~/tls-interop).
    /// </summary>
    internal static class InteropRows
    {
        public const string WwwDir = "www";
        public const string SmallBody = "hello over h2\n";
        public const int LargeBytes = 2 * 1024 * 1024;
        public const int UploadBytes = 1024 * 1024;
        public const int ResponseHeaderCount = 40;
        private const string Host = "127.0.0.1";

        /// <summary>The per-run PKI (shared with tls-interop) and the files the servers serve from <c>www</c>.</summary>
        public static void Generate(string dir)
        {
            TestPki.Generate(dir);
            var www = Path.Combine(dir, WwwDir);
            Directory.CreateDirectory(www);
            File.WriteAllText(Path.Combine(www, "small.txt"), SmallBody);
            File.WriteAllBytes(Path.Combine(www, "large.bin"), Pattern(LargeBytes, 1));
        }

        public static string ResponseHeaderValue(int i) => new string((char)('a' + i % 26), 200);

        public static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)((i * 131 + seed) & 0xFF);
            return bytes;
        }

        public static async Task<int> RunAsync(string dir, string server, int port, string filter)
        {
            string expectedRoots = Path.Combine(dir, TestPki.RootFile);
            if (!string.Equals(Environment.GetEnvironmentVariable("SSL_CERT_FILE"), expectedRoots, StringComparison.Ordinal))
            {
                Console.WriteLine("FAIL precondition: SSL_CERT_FILE must be " + expectedRoots + " (run through Tools~/h2-interop/run.sh)");
                return 1;
            }

            // No Unity bootstrap runs here (TurboHttpBootstrap / RuntimeInitializeOnLoadMethod).
            RawSocketTransport.EnsureRegistered();
            BouncyCastleModuleRegistration.EnsureRegistered();
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Console.WriteLine("FAIL precondition: the BouncyCastle TLS provider is not available (#140)");
                return 1;
            }

            // BouncyCastle has no platform verifier on Linux (#248) and would reject every chain; trust the
            // per-run root through the test-anchor hook (as Tools~/tls-interop does). SslStream ignores it and
            // keeps trusting the root through SSL_CERT_FILE.
            using (var root = new X509Certificate2(expectedRoots))
                ServerCertificateVerifier.AddTestTrustAnchor(root.RawData);

            if (server != "kestrel" && server != "nghttpd" && server != "nginx")
            {
                Console.Error.WriteLine("unknown server " + server + " (kestrel, nghttpd, nginx)");
                return 2;
            }

            KestrelHost kestrel = null;
            try
            {
                if (server == "kestrel")
                {
                    kestrel = await KestrelHost.StartAsync(dir).ConfigureAwait(false);
                    port = kestrel.Port;
                }

                await WaitForListenerAsync(server, port).ConfigureAwait(false);
                var target = new Target(port, echoesUpload: server != "nginx", servesHeaders: server != "nghttpd");
                var rows = new List<(string, Func<CancellationToken, Task>)>();
                foreach (var (name, run) in Rows)
                {
                    if (name == "response-headers-many" && !target.ServesHeaders)
                        continue;
                    string full = "interop/" + server + "/" + name;
                    if (filter == null || full.Contains(filter, StringComparison.Ordinal))
                        rows.Add((full, ct => run(target, ct)));
                }

                return await Program.RunRowsAsync(rows).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL precondition: " + server + " on port " + port + ": " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                if (kestrel != null)
                    await kestrel.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static readonly (string Name, Func<Target, CancellationToken, Task> Run)[] Rows =
        {
            ("get-small", GetSmallAsync),
            ("get-large", GetLargeAsync),
            ("concurrent-100", Concurrent100Async),
            ("post-upload", PostUploadAsync),
            ("request-headers-large", RequestHeadersLargeAsync),
            ("response-headers-many", ResponseHeadersManyAsync),
            ("bc-get-small", BcGetSmallAsync),
            ("bc-connect-get-small", BcConnectGetSmallAsync),
            ("bc-connect-get-large", BcConnectGetLargeAsync),
        };

        private sealed class Target
        {
            public Target(int port, bool echoesUpload, bool servesHeaders)
            {
                Port = port;
                EchoesUpload = echoesUpload;
                ServesHeaders = servesHeaders;
            }

            public int Port { get; }

            /// <summary>nghttpd --echo-upload and the Kestrel endpoint echo POST bodies; nginx answers "uploaded".</summary>
            public bool EchoesUpload { get; }

            /// <summary>Serves <c>/headers</c> with <see cref="ResponseHeaderCount"/> large response fields.</summary>
            public bool ServesHeaders { get; }
        }

        private static async Task GetSmallAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            using var response = await SendAsync(transport, target, HttpMethod.GET, "/small.txt", null, null, ct).ConfigureAwait(false);
            Check(response.GetBodyAsString() == SmallBody, "body: " + response.GetBodyAsString());
            CheckHttp2(transport, target);
        }

        // 2 MiB through the client's receive windows: WINDOW_UPDATE pacing against the server's sender.
        private static async Task GetLargeAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            using var response = await SendAsync(transport, target, HttpMethod.GET, "/large.bin", null, null, ct).ConfigureAwait(false);
            Check(SameBytes(Pattern(LargeBytes, 1), response.Body.ToArray()), "large body differs (" + response.Body.Length + " bytes)");
            CheckHttp2(transport, target);
        }

        // 100 concurrent streams on one connection, around the servers' MAX_CONCURRENT_STREAMS (nghttpd 100, nginx 128).
        private static async Task Concurrent100Async(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            using (await SendAsync(transport, target, HttpMethod.GET, "/small.txt", null, null, ct).ConfigureAwait(false))
            {
            }

            var tasks = new Task<UHttpResponse>[100];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = SendAsync(transport, target, HttpMethod.GET, "/small.txt?i=" + i.ToString(CultureInfo.InvariantCulture), null, null, ct);
            for (int i = 0; i < tasks.Length; i++)
            {
                using var response = await tasks[i].ConfigureAwait(false);
                Check(response.GetBodyAsString() == SmallBody, "request " + i + " body: " + response.GetBodyAsString());
            }

            CheckHttp2(transport, target);
        }

        // 1 MiB upload against the server's receive windows.
        private static async Task PostUploadAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            var body = Pattern(UploadBytes, 7);
            using var response = await SendAsync(transport, target, HttpMethod.POST, "/upload", body, null, ct).ConfigureAwait(false);
            if (target.EchoesUpload)
                Check(SameBytes(body, response.Body.ToArray()), "echoed upload differs (" + response.Body.Length + " bytes)");
            else
                Check(response.GetBodyAsString() == "uploaded\n", "upload response: " + response.GetBodyAsString());
            CheckHttp2(transport, target);
        }

        // 20 x 300-byte request fields twice on one connection: the client's HPACK encoder against the server's decoder.
        private static async Task RequestHeadersLargeAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            for (int round = 0; round < 2; round++)
            {
                var headers = new HttpHeaders();
                for (int i = 0; i < 20; i++)
                    headers.Set("x-client-" + i.ToString(CultureInfo.InvariantCulture), new string((char)('a' + (i + round) % 26), 300));
                using var response = await SendAsync(transport, target, HttpMethod.GET, "/small.txt", null, headers, ct).ConfigureAwait(false);
                Check(response.GetBodyAsString() == SmallBody, "round " + round + " body: " + response.GetBodyAsString());
            }

            CheckHttp2(transport, target);
        }

        // 40 x 200-byte response fields twice: the server's HPACK encoder (dynamic table, eviction) against the client's decoder.
        private static async Task ResponseHeadersManyAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.SslStream);
            for (int round = 0; round < 2; round++)
            {
                using var response = await SendAsync(transport, target, HttpMethod.GET, "/headers", null, null, ct).ConfigureAwait(false);
                for (int i = 0; i < ResponseHeaderCount; i++)
                {
                    string name = "x-h-" + i.ToString(CultureInfo.InvariantCulture);
                    Check(response.Headers.Get(name) == ResponseHeaderValue(i), "round " + round + " " + name + ": " + response.Headers.Get(name));
                }
            }

            CheckHttp2(transport, target);
        }

        // BouncyCastle straight to the server: ALPN h2 without a proxy, so a tunnel failure below is not a BC one.
        private static async Task BcGetSmallAsync(Target target, CancellationToken ct)
        {
            using var transport = new RawSocketTransport(null, TlsBackend.BouncyCastle);
            using var response = await SendAsync(transport, target, HttpMethod.GET, "/small.txt", null, null, ct).ConfigureAwait(false);
            Check(response.GetBodyAsString() == SmallBody, "body: " + response.GetBodyAsString());
            CheckHttp2(transport, target);
        }

        // #255: BouncyCastle through a real CONNECT tunnel. One GET opens the tunnel and negotiates h2 inside it;
        // 20 concurrent GETs then multiplex on that HTTP/2 connection, so the proxy sees exactly one tunnel.
        private static async Task BcConnectGetSmallAsync(Target target, CancellationToken ct)
        {
            await using var proxy = ConnectProxy.Start();
            using (var transport = new RawSocketTransport(null, TlsBackend.BouncyCastle))
            {
                using (var first = await SendAsync(transport, target, HttpMethod.GET, "/small.txt", null, null, ct, proxy).ConfigureAwait(false))
                    Check(first.GetBodyAsString() == SmallBody, "first body: " + first.GetBodyAsString());
                CheckHttp2ViaProxy(transport, target, proxy);

                var tasks = new Task<UHttpResponse>[20];
                for (int i = 0; i < tasks.Length; i++)
                    tasks[i] = SendAsync(transport, target, HttpMethod.GET, "/small.txt?i=" + i.ToString(CultureInfo.InvariantCulture), null, null, ct, proxy);
                for (int i = 0; i < tasks.Length; i++)
                {
                    using var response = await tasks[i].ConfigureAwait(false);
                    Check(response.GetBodyAsString() == SmallBody, "request " + i + " body: " + response.GetBodyAsString());
                }

                CheckHttp2ViaProxy(transport, target, proxy);
            }

            CheckTunnels(proxy, target, 1);
        }

        // 2 MiB through the tunnel: HTTP/2 flow control over BouncyCastle records over the relayed TCP stream.
        private static async Task BcConnectGetLargeAsync(Target target, CancellationToken ct)
        {
            await using var proxy = ConnectProxy.Start();
            using (var transport = new RawSocketTransport(null, TlsBackend.BouncyCastle))
            {
                using (var response = await SendAsync(transport, target, HttpMethod.GET, "/large.bin", null, null, ct, proxy).ConfigureAwait(false))
                    Check(SameBytes(Pattern(LargeBytes, 1), response.Body.ToArray()), "large body differs (" + response.Body.Length + " bytes)");
                CheckHttp2ViaProxy(transport, target, proxy);
            }

            CheckTunnels(proxy, target, 1);
        }

        private static async Task<UHttpResponse> SendAsync(
            RawSocketTransport transport, Target target, HttpMethod method, string path, byte[] body, HttpHeaders headers, CancellationToken ct,
            ConnectProxy proxy = null)
        {
            var uri = new Uri("https://" + Host + ":" + target.Port.ToString(CultureInfo.InvariantCulture) + path);
            var request = new UHttpRequest(method, uri, headers, body);
            if (proxy != null)
            {
                // The transport reads the proxy from request metadata (UHttpClient resolves it there); the leg to
                // the http:// proxy is cleartext, so the request opts in like UHttpClientOptions.AllowCleartext.
                request = request
                    .WithProxy(new ProxySettings
                    {
                        Address = new Uri("http://" + Host + ":" + proxy.Port.ToString(CultureInfo.InvariantCulture)),
                        UseEnvironmentVariables = false,
                    })
                    .WithMetadata(RequestMetadataKeys.AllowCleartext, true);
            }

            var response = await TransportDispatchHelper.CollectResponseAsync(transport, request, new RequestContext(request), ct).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException(method + " " + path + ": status " + status);
            }

            return response;
        }

        private static void CheckHttp2(RawSocketTransport transport, Target target) =>
            Check(transport.HasHttp2Connection(Host, target.Port), "the request did not run on an HTTP/2 connection");

        /// <summary>The HTTP/2 connection exists under the origin x proxy key, and no direct one was opened.</summary>
        private static void CheckHttp2ViaProxy(RawSocketTransport transport, Target target, ConnectProxy proxy)
        {
            Check(transport.HasHttp2Connection(Host, target.Port, Host, proxy.Port),
                "the request did not run on an HTTP/2 connection through the CONNECT tunnel");
            Check(!transport.HasHttp2Connection(Host, target.Port), "an HTTP/2 connection bypassed the proxy");
        }

        private static void CheckTunnels(ConnectProxy proxy, Target target, int expected)
        {
            string authority = Host + ":" + target.Port.ToString(CultureInfo.InvariantCulture);
            var targets = proxy.ConnectTargets;
            Check(proxy.AcceptCount == expected && targets.Length == expected && Array.TrueForAll(targets, t => t == authority),
                "expected " + expected + " CONNECT " + authority + ", proxy saw " + proxy.AcceptCount + " connection(s): " +
                string.Join(", ", targets));
        }

        /// <summary>Readiness of a server started by run.sh (docker) or in-process: TCP accept on the port.</summary>
        private static async Task WaitForListenerAsync(string server, int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    return;
                }
                catch (SocketException) when (attempt < 60)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
