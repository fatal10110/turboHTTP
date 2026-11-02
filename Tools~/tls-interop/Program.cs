// TLS interop job (#230, #113): the production TLS providers (BouncyCastle through the non-blocking
// BouncyCastleTlsStream, platform SslStream, Auto) against OpenSSL 3 s_server on loopback.
//
//   generate <dir>              writes the throwaway PKI (TestPki) into <dir>
//   run <dir> [--row <filter>]  runs every row; needs SSL_CERT_FILE=<dir>/root.pem (run.sh sets it)
//
// Exit 0 when every row passes, 1 when a row fails or a precondition (OpenSSL 3, the BouncyCastle
// provider) is missing, 2 on usage errors. Nothing skips.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.BouncyCastle;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tools.TlsInterop
{
    internal static class Program
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly TlsBackend[] AllBackends = { TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto };

        private static string s_dir;
        private static string s_logDir;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "generate")
            {
                TestPki.Generate(Path.GetFullPath(args[1]));
                return 0;
            }

            if (args.Length < 2 || args[0] != "run" || (args.Length != 2 && (args.Length != 4 || args[2] != "--row")))
            {
                Console.Error.WriteLine("usage: TurboHTTP.TlsInterop generate <dir> | run <dir> [--row <filter>]");
                return 2;
            }

            s_dir = Path.GetFullPath(args[1]);
            s_logDir = Path.Combine(s_dir, "openssl");
            Directory.CreateDirectory(s_logDir);
            string filter = args.Length == 4 ? args[3] : null;

            string expectedRoots = Path.Combine(s_dir, TestPki.RootFile);
            if (!string.Equals(Environment.GetEnvironmentVariable("SSL_CERT_FILE"), expectedRoots, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("FAIL precondition: SSL_CERT_FILE must be " + expectedRoots + " (run through Tools~/tls-interop/run.sh)");
                return 1;
            }

            try
            {
                Console.WriteLine("openssl: " + OpenSslServer.Version);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("FAIL precondition: " + ex.Message);
                return 1;
            }

            // No Unity bootstrap runs here (TurboHttpBootstrap / RuntimeInitializeOnLoadMethod).
            RawSocketTransport.EnsureRegistered();
            BouncyCastleModuleRegistration.EnsureRegistered();
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Console.Error.WriteLine("FAIL precondition: the BouncyCastle TLS provider is not available (#140)");
                return 1;
            }

            var rows = BuildRows();
            int failed = 0, ran = 0;
            foreach (var (name, body) in rows)
            {
                if (filter != null && name.IndexOf(filter, StringComparison.Ordinal) < 0)
                    continue;
                ran++;
                try
                {
                    string detail = await body().ConfigureAwait(false);
                    Console.WriteLine("PASS " + name + (detail == null ? string.Empty : " (" + detail + ")"));
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + name + ": " + ex);
                }
            }

            Console.WriteLine($"{ran - failed}/{ran} rows passed");
            return ran == 0 || failed > 0 ? 1 : 0;
        }

        private static List<(string, Func<Task<string>>)> BuildRows()
        {
            var rows = new List<(string, Func<Task<string>>)>
            {
                // #230: one AES-256-GCM suite per version, through BouncyCastleTlsStream (RFC 5289, RFC 8446 B.4).
                ("bc/tls1.2/ECDHE-RSA-AES256-GCM-SHA384", () => PinnedSuiteAsync(
                    "1.2", "0xC030", "ECDHE-RSA-AES256-GCM-SHA384", "-tls1_2", "-cipher", "ECDHE-RSA-AES256-GCM-SHA384")),
                ("bc/tls1.3/TLS_AES_256_GCM_SHA384", () => PinnedSuiteAsync(
                    "1.3", "0x1302", "TLS_AES_256_GCM_SHA384", "-tls1_3", "-ciphersuites", "TLS_AES_256_GCM_SHA384")),
                // The TLS 1.1 server of the tls-below-1.2 rows really speaks TLS 1.1.
                ("control/tls1.1-server-accepts-tls1.1-client", Tls11ServerControlAsync),
            };

            foreach (var backend in AllBackends)
            {
                string p = backend.ToString().ToLowerInvariant();

                // Positive controls: the root is trusted, so each negative row below fails for its own defect.
                rows.Add((p + "/accept/valid@localhost", () => ExpectOkAsync(backend, "localhost", "valid")));
                rows.Add((p + "/accept/valid@127.0.0.1", () => ExpectOkAsync(backend, "127.0.0.1", "valid")));
                rows.Add((p + "/accept/intermediate-sent@localhost", () => ExpectOkAsync(
                    backend, "localhost", "via-intermediate", "-cert_chain", Path.Combine(s_dir, TestPki.IntermediateFile))));

                // #113 negative suite.
                rows.Add((p + "/reject/self-signed", () => ExpectCertificateErrorAsync(backend, "localhost", "self-signed")));
                rows.Add((p + "/reject/untrusted-root", () => ExpectCertificateErrorAsync(backend, "localhost", "untrusted-root")));
                rows.Add((p + "/reject/expired", () => ExpectCertificateErrorAsync(backend, "localhost", "expired")));
                rows.Add((p + "/reject/not-yet-valid", () => ExpectCertificateErrorAsync(backend, "localhost", "not-yet-valid")));
                rows.Add((p + "/reject/wrong-host", () => ExpectCertificateErrorAsync(backend, "localhost", "wrong-host")));
                rows.Add((p + "/reject/cn-only-with-san", () => ExpectCertificateErrorAsync(backend, "localhost", "cn-only-with-san")));
                rows.Add((p + "/reject/ip-host-without-ip-san", () => ExpectCertificateErrorAsync(backend, "127.0.0.1", "ip-in-dns-san")));
                rows.Add((p + "/reject/missing-intermediate", () => ExpectCertificateErrorAsync(backend, "localhost", "via-intermediate")));
                rows.Add((p + "/reject/tld-wildcard@example.com", () => ExpectProviderRejectsAsync(backend, "example.com", "tld-wildcard")));
                rows.Add((p + "/reject/tls1.1-server", () => ExpectCertificateErrorAsync(
                    backend, "localhost", "valid", "-tls1_1", "-cipher", "DEFAULT:@SECLEVEL=0")));
            }

            return rows;
        }

        private static string[] Identity(string leaf, string[] extra)
        {
            var flags = new List<string> { "-cert", TestPki.Leaf(s_dir, leaf), "-key", Path.Combine(s_dir, TestPki.KeyFile) };
            flags.AddRange(extra);
            return flags.ToArray();
        }

        private static string LogName(string leaf, string[] extra)
        {
            return leaf + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + (extra.Length > 0 ? "-" + extra[0].TrimStart('-') : string.Empty);
        }

        private static async Task<string> PinnedSuiteAsync(string version, string suite, string opensslName, params string[] flags)
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var all = new List<string>(flags) { "-www" };
            using var server = OpenSslServer.Start(s_logDir, "pinned-" + opensslName, Identity("valid", all.ToArray()));

            var stream = await ConnectAsync(server.Port).ConfigureAwait(false);
            TlsResult result;
            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                result = await provider.WrapAsync(stream, "localhost", new[] { "http/1.1" }, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            string response;
            using (var secure = result.SecureStream)
            {
                Check(result.ProviderName == "BouncyCastle", "provider " + result.ProviderName);
                Check(result.TlsVersion == version, "TLS version " + result.TlsVersion + ", expected " + version);
                Check(result.CipherSuite == suite, "cipher suite " + result.CipherSuite + ", expected " + suite);
                Check(secure.GetType().Name == "BouncyCastleTlsStream", "secure stream " + secure.GetType().FullName);

                using var cts = new CancellationTokenSource(Timeout);
                byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                await secure.WriteAsync(request, 0, request.Length, cts.Token).ConfigureAwait(false);
                await secure.FlushAsync(cts.Token).ConfigureAwait(false);
                response = await ReadToEndAsync(secure, cts.Token).ConfigureAwait(false);
            }

            Check(response.StartsWith("HTTP/1.0 200", StringComparison.Ordinal) || response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
                "response: " + Head(response));
            // s_server -www reports the negotiated cipher in its page: the server agrees on the suite.
            Check(response.Contains(opensslName, StringComparison.Ordinal), "server page does not name " + opensslName + ": " + Head(response));
            server.WaitForExit();
            return "TLS " + result.TlsVersion + " " + result.CipherSuite + ", " + response.Length + " response bytes";
        }

        private static Task<string> Tls11ServerControlAsync()
        {
            using var server = OpenSslServer.Start(s_logDir, "tls11-control",
                Identity("valid", new[] { "-tls1_1", "-cipher", "DEFAULT:@SECLEVEL=0", "-www" }));
            string output = OpenSslServer.Run("s_client", "-connect", "127.0.0.1:" + server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-tls1_1", "-cipher", "DEFAULT:@SECLEVEL=0", "-servername", "localhost");
            Check(output.Contains("Protocol  : TLSv1.1", StringComparison.Ordinal) || output.Contains("TLSv1.1", StringComparison.Ordinal),
                "s_client did not negotiate TLS 1.1: " + Head(output));
            server.WaitForExit();
            return Task.FromResult("openssl s_client negotiated TLSv1.1");
        }

        private static async Task<string> ExpectOkAsync(TlsBackend backend, string host, string leaf, params string[] extra)
        {
            var all = new List<string>(extra) { "-www" };
            using var server = OpenSslServer.Start(s_logDir, LogName(leaf, extra), Identity(leaf, all.ToArray()));
            int status = await SendAsync(backend, host, server.Port).ConfigureAwait(false);
            Check(status == 200, "status " + status);
            server.WaitForExit();
            return "HTTP " + status;
        }

        private static async Task<string> ExpectCertificateErrorAsync(TlsBackend backend, string host, string leaf, params string[] extra)
        {
            // -naccept 1: a retry or a provider fallback on a second connection is refused, which surfaces
            // as a network error, not as the certificate error this row requires.
            var all = new List<string>(extra) { "-www" };
            using var server = OpenSslServer.Start(s_logDir, LogName(leaf, extra), Identity(leaf, all.ToArray()));
            try
            {
                int status = await SendAsync(backend, host, server.Port).ConfigureAwait(false);
                throw new InvalidOperationException("accepted: HTTP " + status);
            }
            catch (UHttpException ex)
            {
                Check(ex.HttpError.Type == UHttpErrorType.CertificateError, "error " + ex.HttpError.Type + ": " + ex.Message);
                server.WaitForExit();
                return "CertificateError: " + FirstLine(ex.Message);
            }
        }

        private static async Task<string> ExpectProviderRejectsAsync(TlsBackend backend, string host, string leaf)
        {
            // The client resolves its request host, so a host that is not loopback (example.com) drives the
            // provider directly over a loopback socket. AuthenticationException is exactly what
            // RawSocketTransport maps to UHttpErrorType.CertificateError.
            var provider = TlsProviderSelector.GetProvider(backend);
            using var server = OpenSslServer.Start(s_logDir, LogName(leaf, Array.Empty<string>()), Identity(leaf, new[] { "-www" }));
            var stream = await ConnectAsync(server.Port).ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                var result = await provider.WrapAsync(stream, host, new[] { "http/1.1" }, cts.Token).ConfigureAwait(false);
                result.SecureStream.Dispose();
                throw new InvalidOperationException("accepted " + leaf + " for " + host);
            }
            catch (AuthenticationException ex)
            {
                stream.Dispose();
                server.WaitForExit();
                return "AuthenticationException: " + FirstLine(ex.Message);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// One GET through <see cref="RawSocketTransport"/> (pool, TLS provider selection, HTTP/1.1 or h2),
        /// the layer that maps an <see cref="AuthenticationException"/> to <see cref="UHttpErrorType.CertificateError"/>.
        /// UHttpClient itself is not used: the editor shadow build of TurboHTTP.Core calls into the Unity
        /// engine while building its interceptors, which a plain .NET process cannot.
        /// </summary>
        private static async Task<int> SendAsync(TlsBackend backend, string host, int port)
        {
            using var transport = new RawSocketTransport(null, backend);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(Url(host, port))).WithTimeout(Timeout);
            var handler = new StatusHandler();
            using var cts = new CancellationTokenSource(Timeout);
            await transport.DispatchAsync(request, handler, new RequestContext(request), cts.Token).ConfigureAwait(false);
            if (handler.Error != null)
                throw handler.Error;
            Check(handler.Status != 0, "no response");
            return handler.Status;
        }

        private sealed class StatusHandler : IHttpHandler
        {
            public int Status;
            public UHttpException Error;

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(int statusCode, HttpHeaders headers, IResponseBodySource body, RequestContext context)
            {
                Status = statusCode;
                await using (body)
                {
                    using var cts = new CancellationTokenSource(Timeout);
                    await body.DrainAsync(cts.Token).ConfigureAwait(false);
                }
            }

            public void OnResponseError(UHttpException error, RequestContext context) => Error = error;
        }

        private static async Task<Stream> ConnectAsync(int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var all = new MemoryStream();
            while (all.Length < 1024 * 1024)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                all.Write(buffer, 0, n);
            }

            return Encoding.ASCII.GetString(all.GetBuffer(), 0, (int)all.Length);
        }

        private static string Url(string host, int port) => "https://" + host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

        private static string Head(string text) => text.Length <= 400 ? text : text.Substring(0, 400);

        private static string FirstLine(string text)
        {
            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text.Substring(0, newline);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
