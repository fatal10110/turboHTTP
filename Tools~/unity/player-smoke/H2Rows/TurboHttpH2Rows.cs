// #1210: the HTTP/2-over-TLS rows of Tests/Runtime/Integration/Http2TlsBackendRowTests.cs (#885) in the player
// smoke: 2 MiB download, 1 MiB echoed upload, 100 concurrent streams after a warm-up request, and a streaming read
// cancelled mid-body followed by a second 2 MiB download, per TLS provider, through the production pool and
// RawSocketTransport. The large and concurrent transfers are what exercise flow control (RFC 9113 5.2, 6.9: the
// WINDOW_UPDATE frames the read loop sends through the provider's TLS stream) on the player's runtime.
//
// This folder is its own assembly (TurboHTTP.PlayerSmoke.H2; the rest of the smoke stays in Assembly-CSharp and
// calls it): the rows use two test-only Transport internals, which Runtime/Transport/AssemblyInfo.cs makes visible
// to this assembly only. ServerCertificateVerifier.AddTestTrustAnchor makes the throwaway root of
// Tools~/h2-interop/run.sh --serve one more trust anchor for TurboHTTP's verifier (BouncyCastle), for the duration
// of one row: the chain, validity and host-name checks still run, and nothing else is trusted. SslStream validates
// against the platform store only, so with a root its rows are not run here (the caller reports them SKIP).
// RawSocketTransport.HasHttp2Connection is the strict-h2 assertion: BouncyCastle always offers ALPN h2 and the
// server selects it (RFC 9113 3.2), so a BouncyCastle row that ran over HTTP/1.1 fails.
using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.PlayerSmoke
{
    public static class TurboHttpH2Rows
    {
        public const string Issue = "#1210";

        // Row names, in run order; the row id is h2-<name>-<provider> (RowId).
        public static readonly string[] Rows = { "download", "upload", "concurrent", "stream-cancel" };

        // The h2-interop files (Tools~/h2-interop/InteropRows.cs): small.txt, large.bin = Pattern(2 MiB, 1); the
        // upload body is Pattern(1 MiB, 7), echoed by POST /upload.
        private const string SmallBody = "hello over h2\n";
        private const int LargeBytes = 2 * 1024 * 1024;
        private const int UploadBytes = 1024 * 1024;
        private const int ConcurrentStreams = 100;
        private const int StreamingPrefixBytes = 64 * 1024;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        public static string RowId(string row, TlsBackend backend)
            => "h2-" + row + "-" + (backend == TlsBackend.BouncyCastle ? "bouncycastle" : "sslstream");

        /// <summary>
        /// Runs <paramref name="row"/> through <paramref name="backend"/> against <paramref name="url"/> (the base
        /// URL of an h2-interop file server). <paramref name="rootBase64"/>, when set, is the server's DER root in
        /// base64, trusted by TurboHTTP's verifier for this row only (BouncyCastle). Returns the row detail; throws
        /// on any failure.
        /// </summary>
        public static async Task<string> RunAsync(
            string row, TlsBackend backend, string url, string rootBase64, CancellationToken ct)
        {
            if (!Uri.TryCreate((url ?? string.Empty).TrimEnd('/'), UriKind.Absolute, out var server) ||
                server.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("the HTTP/2 row server URL must be an absolute https URL, got '" + url + "'");

            byte[][] saved = null;
            if (!string.IsNullOrEmpty(rootBase64))
            {
                if (backend != TlsBackend.BouncyCastle)
                    throw new InvalidOperationException(
                        "SslStream validates against the platform store only; a test root applies to BouncyCastle");
                var der = Convert.FromBase64String(rootBase64);
                saved = ServerCertificateVerifier.SnapshotTestTrustAnchors();
                ServerCertificateVerifier.AddTestTrustAnchor(der);
            }

            try
            {
                using (var pool = new TcpConnectionPool(tlsBackend: backend))
                using (var transport = new RawSocketTransport(pool))
                using (var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false
                }))
                {
                    var baseUrl = server.GetLeftPart(UriPartial.Path).TrimEnd('/');
                    var watch = Stopwatch.StartNew();
                    string detail;
                    switch (row)
                    {
                        case "download":
                            await GetLargeAsync(client, baseUrl, ct);
                            detail = "bytes=" + LargeBytes;
                            break;
                        case "upload":
                            detail = await UploadAsync(client, baseUrl, ct);
                            break;
                        case "concurrent":
                            detail = await ConcurrentAsync(client, baseUrl, ct);
                            break;
                        case "stream-cancel":
                            detail = await StreamingCancelAsync(client, baseUrl, ct);
                            break;
                        default:
                            throw new ArgumentException("unknown row " + row);
                    }

                    var elapsed = watch.ElapsedMilliseconds;
                    return "backend=" + backend + " protocol=" + Protocol(backend, transport, server) + " " + detail +
                           " ms=" + elapsed.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                if (saved != null)
                    ServerCertificateVerifier.RestoreTestTrustAnchors(saved);
            }
        }

        // RawSocketTransport opens an HTTP/2 connection only on ALPN h2 and keeps it until disposed.
        private static string Protocol(TlsBackend backend, RawSocketTransport transport, Uri server)
        {
            var h2 = transport.HasHttp2Connection(server.Host, server.Port);
            if (backend == TlsBackend.BouncyCastle && !h2)
            {
                throw new InvalidOperationException(
                    "BouncyCastle offers ALPN h2 and the server selects it, but the row ran without an HTTP/2 " +
                    "connection (HTTP/1.1 fallback, RFC 9113 3.2)");
            }

            return h2 ? "h2" : "http/1.1";
        }

        private static async Task GetLargeAsync(UHttpClient client, string baseUrl, CancellationToken ct)
        {
            using (var response = await client.Get(baseUrl + "/large.bin")
                       .WithTimeout(RequestTimeout)
                       .SendBufferedAsync(ct))
            {
                RequireOk(response.StatusCode, "/large.bin");
                RequirePattern(response.Body, LargeBytes, 1, "large.bin");
            }
        }

        private static async Task<string> UploadAsync(UHttpClient client, string baseUrl, CancellationToken ct)
        {
            using (var response = await client.Post(baseUrl + "/upload")
                       .WithBody(Pattern(UploadBytes, 7))
                       .WithTimeout(RequestTimeout)
                       .SendBufferedAsync(ct))
            {
                RequireOk(response.StatusCode, "/upload");
                RequirePattern(response.Body, UploadBytes, 7, "upload echo");
            }

            return "bytes=" + UploadBytes;
        }

        private static async Task<string> ConcurrentAsync(UHttpClient client, string baseUrl, CancellationToken ct)
        {
            // The warm-up opens the connection the concurrent streams share.
            using (var warmUp = await GetSmallAsync(client, baseUrl + "/small.txt", ct))
            {
                RequireSmall(warmUp, "warm-up");
            }

            var tasks = new Task<UHttpResponse>[ConcurrentStreams];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = GetSmallAsync(client, baseUrl + "/small.txt?i=" + i.ToString(CultureInfo.InvariantCulture), ct);

            try
            {
                await Task.WhenAll(tasks);
                for (int i = 0; i < tasks.Length; i++)
                    RequireSmall(await tasks[i], "request " + i.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                // Every response that arrived is disposed, also when another one failed.
                foreach (var task in tasks)
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        (await task).Dispose();
                }
            }

            return "streams=" + ConcurrentStreams;
        }

        // A streaming read cancelled mid-body, then the response disposed (RST_STREAM on h2): the connection must stay
        // usable, so the credit of the abandoned stream is returned and a second 2 MiB download completes.
        private static async Task<string> StreamingCancelAsync(UHttpClient client, string baseUrl, CancellationToken ct)
        {
            int total = 0;
            var streaming = await client.Get(baseUrl + "/large.bin")
                .WithTimeout(RequestTimeout)
                .SendStreamingAsync(ct);
            try
            {
                RequireOk(streaming.StatusCode, "/large.bin (streaming)");
                var buffer = new byte[16 * 1024];
                while (total < StreamingPrefixBytes)
                {
                    int read = await streaming.Body.ReadAsync(buffer.AsMemory(), ct);
                    if (read <= 0)
                        throw new InvalidOperationException("the body ended after " + total + " of " + LargeBytes + " bytes");
                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] != PatternByte(total + i, 1))
                            throw new InvalidOperationException("streamed body differs at byte " + (total + i));
                    }

                    total += read;
                }

                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    bool cancelled = false;
                    try
                    {
                        while (await streaming.Body.ReadAsync(buffer.AsMemory(), cancel.Token) > 0)
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

                    if (!cancelled)
                        throw new InvalidOperationException("a read with a cancelled token drained the body instead of cancelling");
                }
            }
            finally
            {
                await streaming.DisposeAsync();
            }

            await GetLargeAsync(client, baseUrl, ct);
            return "prefix=" + total + " then bytes=" + LargeBytes;
        }

        private static async Task<UHttpResponse> GetSmallAsync(UHttpClient client, string url, CancellationToken ct)
        {
            return await client.Get(url).WithTimeout(RequestTimeout).SendBufferedAsync(ct);
        }

        private static void RequireSmall(UHttpResponse response, string what)
        {
            RequireOk(response.StatusCode, what);
            var body = Encoding.UTF8.GetString(response.Body.ToArray());
            if (body != SmallBody)
                throw new InvalidOperationException(what + ": small.txt body differs (" + body.Length + " chars)");
        }

        private static void RequireOk(HttpStatusCode status, string what)
        {
            if (status != HttpStatusCode.OK)
                throw new InvalidOperationException(what + ": status " + (int)status + ", expected 200");
        }

        private static void RequirePattern(ReadOnlySequence<byte> body, int length, int seed, string what)
        {
            if (body.Length != length)
                throw new InvalidOperationException(what + ": " + body.Length + " bytes, expected " + length);

            int offset = 0;
            foreach (var segment in body)
            {
                var span = segment.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    if (span[i] != PatternByte(offset + i, seed))
                        throw new InvalidOperationException(what + " differs at byte " + (offset + i));
                }

                offset += span.Length;
            }
        }

        private static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = PatternByte(i, seed);
            return bytes;
        }

        private static byte PatternByte(int index, int seed) => (byte)((index * 131 + seed) & 0xFF);
    }
}
