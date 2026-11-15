#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport;
using TurboHTTP.Tests.Transport.Tls;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Integration
{
    /// <summary>
    /// #885: the full HTTP/2-over-TLS path on a Unity runtime (Editor, players), per TLS backend,
    /// through the production pool and RawSocketTransport: the h2-interop rows (Tools~/h2-interop)
    /// that otherwise run only on .NET 8. Each row runs against the server named by
    /// <c>TURBOHTTP_H2_ROWS_URL</c> (base URL serving <c>/small.txt</c>, <c>/large.bin</c> and an
    /// echoing <c>POST /upload</c>, with the h2-interop files). <c>Tools~/h2-interop/run.sh --serve</c>
    /// starts one on loopback (Kestrel, h2 and HTTP/1.1 over TLS) and prints both variables.
    /// <c>TURBOHTTP_H2_ROWS_ROOT</c> names its throwaway root (PEM or DER), which BouncyCastle trusts
    /// through the test-anchor hook; SslStream ignores that hook, so the SslStream rows run only
    /// against a publicly trusted server (the variable unset). Unset URL: every row is skipped.
    /// BouncyCastle always offers ALPN h2, so its rows fail on the HTTP/1.1 fallback the server also
    /// serves (<see cref="AlpnAssert.RequireHttp2"/>). SslStream has no ALPN on Unity's Mono class
    /// library (mono#12880), so its rows assert that fallback (<see cref="AlpnAssert.UsedHttp2"/>).
    /// </summary>
    [TestFixture]
    public class Http2TlsBackendRowTests
    {
        public const string UrlVariable = "TURBOHTTP_H2_ROWS_URL";
        public const string RootVariable = "TURBOHTTP_H2_ROWS_ROOT";

        // The h2-interop files (Tools~/h2-interop/InteropRows.cs): small.txt, large.bin = Pattern(2 MiB, 1),
        // and the upload body Pattern(1 MiB, 7).
        private const string SmallBody = "hello over h2\n";
        private const int LargeBytes = 2 * 1024 * 1024;
        private const int UploadBytes = 1024 * 1024;
        private const int ConcurrentStreams = 100;
        private const int StreamingPrefixBytes = 64 * 1024;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
            HttpTransportFactory.Reset();
        }

        // 2 MiB through the client's receive windows: WINDOW_UPDATE pacing over the backend's TLS stream.
        [TestCase(TlsBackend.SslStream)]
        [TestCase(TlsBackend.BouncyCastle)]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void Download2MiB(TlsBackend backend)
        {
            var server = RequireServer(backend);
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: backend);
                using var transport = new RawSocketTransport(pool);
                using var client = NewClient(transport);

                await GetLargeAsync(client, server, CancellationToken.None).ConfigureAwait(false);
                AssertProtocol(backend, pool, transport, server);
            }).GetAwaiter().GetResult();
        }

        // 1 MiB against the server's receive windows, echoed back.
        [TestCase(TlsBackend.SslStream)]
        [TestCase(TlsBackend.BouncyCastle)]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void Upload1MiB(TlsBackend backend)
        {
            var server = RequireServer(backend);
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: backend);
                using var transport = new RawSocketTransport(pool);
                using var client = NewClient(transport);
                var body = Pattern(UploadBytes, 7);

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody(body)
                    .WithTimeout(RequestTimeout)
                    .SendBufferedAsync()
                    .ConfigureAwait(false);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                AssertSameBytes(body, response.Body.ToArray(), "upload echo");
                AssertProtocol(backend, pool, transport, server);
            }).GetAwaiter().GetResult();
        }

        // 100 concurrent streams on the connection the first request opened (MAX_CONCURRENT_STREAMS 100 on the
        // --serve Kestrel; a lower server limit relies on the client queueing).
        [TestCase(TlsBackend.SslStream)]
        [TestCase(TlsBackend.BouncyCastle)]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void Concurrent100Streams(TlsBackend backend)
        {
            var server = RequireServer(backend);
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: backend);
                using var transport = new RawSocketTransport(pool);
                using var client = NewClient(transport);

                using (await GetSmallAsync(client, server, "/small.txt").ConfigureAwait(false))
                {
                }

                var tasks = new Task<UHttpResponse>[ConcurrentStreams];
                for (int i = 0; i < tasks.Length; i++)
                    tasks[i] = GetSmallAsync(client, server, "/small.txt?i=" + i.ToString(CultureInfo.InvariantCulture));

                for (int i = 0; i < tasks.Length; i++)
                {
                    using var response = await tasks[i].ConfigureAwait(false);
                    Assert.AreEqual(SmallBody, response.GetBodyAsString(), "request " + i);
                }

                AssertProtocol(backend, pool, transport, server);
            }).GetAwaiter().GetResult();
        }

        // A streaming read cancelled mid-body, then the response disposed (RST_STREAM on h2): the connection
        // must stay usable, so the credit of the abandoned stream is returned and a second 2 MiB download completes.
        [TestCase(TlsBackend.SslStream)]
        [TestCase(TlsBackend.BouncyCastle)]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void StreamingReadWithCancel(TlsBackend backend)
        {
            var server = RequireServer(backend);
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: backend);
                using var transport = new RawSocketTransport(pool);
                using var client = NewClient(transport);
                var expected = Pattern(LargeBytes, 1);

                using (var cts = new CancellationTokenSource())
                {
                    var streaming = await client.Get(server.Url("/large.bin"))
                        .WithTimeout(RequestTimeout)
                        .SendStreamingAsync()
                        .ConfigureAwait(false);
                    try
                    {
                        Assert.AreEqual(HttpStatusCode.OK, streaming.StatusCode);
                        var buffer = new byte[16 * 1024];
                        int total = 0;
                        while (total < StreamingPrefixBytes)
                        {
                            int read = await streaming.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None)
                                .ConfigureAwait(false);
                            Assert.Greater(read, 0, "the body ended after " + total + " of " + LargeBytes + " bytes");
                            for (int i = 0; i < read; i++)
                            {
                                if (buffer[i] != expected[total + i])
                                    Assert.Fail("streamed body differs at byte " + (total + i));
                            }

                            total += read;
                        }

                        cts.Cancel();
                        bool cancelled = false;
                        try
                        {
                            while (await streaming.Body.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false) > 0)
                            {
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                        }
                        catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Cancelled)
                        {
                            cancelled = true;
                        }

                        Assert.IsTrue(cancelled, "a read with a cancelled token drained the body instead of cancelling");
                    }
                    finally
                    {
                        await streaming.DisposeAsync().ConfigureAwait(false);
                    }
                }

                await GetLargeAsync(client, server, CancellationToken.None).ConfigureAwait(false);
                AssertProtocol(backend, pool, transport, server);
            }).GetAwaiter().GetResult();
        }

        private static UHttpClient NewClient(RawSocketTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });
        }

        private static async Task GetLargeAsync(UHttpClient client, Server server, CancellationToken ct)
        {
            using var response = await client.Get(server.Url("/large.bin"))
                .WithTimeout(RequestTimeout)
                .SendBufferedAsync(ct)
                .ConfigureAwait(false);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertSameBytes(Pattern(LargeBytes, 1), response.Body.ToArray(), "large.bin");
        }

        private static async Task<UHttpResponse> GetSmallAsync(UHttpClient client, Server server, string path)
        {
            var response = await client.Get(server.Url(path))
                .WithTimeout(RequestTimeout)
                .SendBufferedAsync()
                .ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            return response;
        }

        /// <summary>
        /// BouncyCastle: HTTP/2, no fallback. SslStream: HTTP/2 where the runtime has ALPN, else the asserted
        /// HTTP/1.1 fallback. RawSocketTransport opens an HTTP/2 connection only on ALPN h2, and that connection
        /// never returns to the idle pool, so the pool reports the ALPN result only of HTTP/1.1 connections.
        /// </summary>
        private static void AssertProtocol(TlsBackend backend, TcpConnectionPool pool, RawSocketTransport transport, Server server)
        {
            var provider = TlsProviderSelector.GetProvider(backend);
            bool usedHttp2 = transport.HasHttp2Connection(server.Host, server.Port);
            string idleAlpn = pool.TryGetNegotiatedAlpnProtocol(server.Host, out var alpn) ? alpn : null;

            if (backend == TlsBackend.BouncyCastle)
                AlpnAssert.RequireHttp2(provider, usedHttp2, idleAlpn, server.Host);
            else
                AlpnAssert.UsedHttp2(provider, usedHttp2 ? "h2" : idleAlpn, usedHttp2, server.Host);
        }

        private Server RequireServer(TlsBackend backend)
        {
            if (backend == TlsBackend.BouncyCastle)
                TestRequirements.RequireBouncyCastle();

            var url = Environment.GetEnvironmentVariable(UrlVariable);
            if (string.IsNullOrEmpty(url))
            {
                TestRequirements.IgnoreEnvironment(
                    UrlVariable + " is unset: no HTTP/2-over-TLS row server (start one with Tools~/h2-interop/run.sh --serve).");
            }

            if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                Assert.Fail(UrlVariable + " must be an absolute https URL, got '" + url + "'.");

            var root = Environment.GetEnvironmentVariable(RootVariable);
            if (!string.IsNullOrEmpty(root))
            {
                if (backend == TlsBackend.SslStream)
                {
                    TestRequirements.IgnoreEnvironment(
                        "SslStream does not use the test trust anchor in " + RootVariable +
                        "; its rows need a publicly trusted server (" + RootVariable + " unset).");
                }

                ServerCertificateVerifier.AddTestTrustAnchor(ReadCertificate(root));
            }

            return new Server(baseUri);
        }

        private static byte[] ReadCertificate(string path)
        {
            if (!File.Exists(path))
                Assert.Fail(RootVariable + " names a missing file: " + path);

            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes);
            const string Begin = "-----BEGIN CERTIFICATE-----";
            const string End = "-----END CERTIFICATE-----";
            int start = text.IndexOf(Begin, StringComparison.Ordinal);
            if (start < 0)
                return bytes;

            start += Begin.Length;
            int end = text.IndexOf(End, start, StringComparison.Ordinal);
            if (end < 0)
                Assert.Fail(RootVariable + " has an unterminated PEM certificate: " + path);
            return Convert.FromBase64String(text.Substring(start, end - start));
        }

        private static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)((i * 131 + seed) & 0xFF);
            return bytes;
        }

        private static void AssertSameBytes(byte[] expected, byte[] actual, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + " length");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail(what + " differs at byte " + i);
            }
        }

        private sealed class Server
        {
            private readonly string _base;

            public Server(Uri baseUri)
            {
                _base = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                Host = baseUri.Host;
                Port = baseUri.Port;
            }

            public string Host { get; }

            public int Port { get; }

            public string Url(string path) => _base + path;
        }
    }
}
#endif
