using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// The proxy decision is made per dispatch against the URI actually sent: request-level
    /// <see cref="ProxySettings"/> are validated like client-level ones (#828), and a redirect hop
    /// re-applies the bypass and scheme rules for its own target (#832).
    /// </summary>
    [TestFixture]
    public class RawSocketTransportRequestProxyPolicyTests
    {
        private static readonly string[] ProxyEnvironmentVariables =
        {
            "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "no_proxy", "NO_PROXY"
        };

        private readonly Dictionary<string, string> _savedEnvironment = new Dictionary<string, string>();

        [SetUp]
        public void SetUp()
        {
            _savedEnvironment.Clear();
            foreach (var name in ProxyEnvironmentVariables)
                _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);

            // Lower-case first: on Windows the names are case-insensitive, so clearing a lower-case
            // name after setting the upper-case one would delete the value again.
            foreach (var name in ProxyEnvironmentVariables)
                Environment.SetEnvironmentVariable(name, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var name in ProxyEnvironmentVariables)
                Environment.SetEnvironmentVariable(name, _savedEnvironment[name]);
        }

        // ------------------------------------------------------------ #828: request-level validation

        [TestCase("https://proxy.example:8443")]
        [TestCase("https://proxy.example")]
        [TestCase("socks5://proxy.example:1080")]
        public void WithProxy_NonHttpAddress_ThrowsAtCallSite(string address)
        {
            using var client = CreateClient();
            using var request = client.Get("http://origin.example/resource");

            Assert.Throws<ArgumentException>(
                () => request.WithProxy(new ProxySettings { Address = new Uri(address) }));
        }

        [Test]
        public void WithProxy_CredentialsWithoutAddress_ThrowsAtCallSite()
        {
            using var client = CreateClient();
            using var request = client.Get("http://origin.example/resource");

            Assert.Throws<ArgumentException>(() => request.WithProxy(new ProxySettings
            {
                Credentials = new NetworkCredential("user", "pass")
            }));
        }

        [TestCase("https")]
        [TestCase("socks5")]
        public void RequestMetadataProxy_NonHttpAddress_IsRejectedBeforeAnyConnection(string scheme)
        {
            AssertAsync.Run(async () =>
            {
                // A listener on the proxy port: the old code dialed it in cleartext.
                using var proxyPort = new RecordingHttpServer(closeOnAccept: true);
                using var client = CreateClient();
                var address = scheme + "://user:secret@127.0.0.1:" + proxyPort.Port;

                // Set without WithProxy, so only the transport boundary can catch it.
                var request = client.Get("http://127.0.0.1:1/resource")
                    .WithMetadata(RequestMetadataKeys.ProxySettings, new ProxySettings { Address = new Uri(address) });

                var ex = await CaptureFailureAsync(request.SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.DoesNotContain("127.0.0.1", ex.Message);
                StringAssert.DoesNotContain("secret", ex.Message);
                StringAssert.DoesNotContain(proxyPort.Port.ToString(), ex.Message);
                Assert.AreEqual(0, proxyPort.AcceptCount, "a non-http proxy must never be dialed");
            });
        }

        [Test]
        public void WithProxy_NullAddress_SendsDirectly()
        {
            AssertAsync.Run(async () =>
            {
                using var origin = new RecordingHttpServer(_ => Ok("direct"));
                using var client = CreateClient();

                using var response = await client.Get(origin.Url("/resource"))
                    .WithProxy(new ProxySettings { Address = null, UseEnvironmentVariables = false })
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new[] { "GET /resource HTTP/1.1" }, origin.RequestLines);
            });
        }

        // ------------------------------------------------------------ #832: per-dispatch decision

        [Test]
        public void WithProxy_BypassListMatchesTarget_SendsDirectly()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new RecordingHttpServer(_ => Ok("proxied"));
                using var origin = new RecordingHttpServer(_ => Ok("direct"));
                using var client = CreateClient();

                using var response = await client.Get(origin.Url("/resource"))
                    .WithProxy(new ProxySettings
                    {
                        Address = new Uri("http://127.0.0.1:" + proxy.Port),
                        BypassList = new[] { "127.0.0.1" },
                        UseEnvironmentVariables = false
                    })
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new[] { "GET /resource HTTP/1.1" }, origin.RequestLines);
                Assert.AreEqual(0, proxy.RequestLines.Length, "a bypassed target must not reach the proxy");
            });
        }

        [Test]
        public void WithProxy_RedirectIntoBypassList_FollowUpGoesDirect()
        {
            AssertAsync.Run(async () =>
            {
                using var origin = new RecordingHttpServer(_ => Ok("direct"));
                using var proxy = new RecordingHttpServer(line => line.Contains("public.example/start")
                    ? Redirect(origin.Url("/landing"))
                    : Ok("proxied"));
                using var client = CreateClient();

                using var response = await client.Get("http://public.example/start")
                    .WithProxy(new ProxySettings
                    {
                        Address = new Uri("http://127.0.0.1:" + proxy.Port),
                        BypassList = new[] { "127.0.0.1" },
                        UseEnvironmentVariables = false
                    })
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new[] { "GET http://public.example/start HTTP/1.1" }, proxy.RequestLines);
                Assert.AreEqual(new[] { "GET /landing HTTP/1.1" }, origin.RequestLines);
            });
        }

        // Case (a): NO_PROXY + HTTP_PROXY, a proxied URI redirects to a bypassed one.
        [Test]
        public void EnvironmentProxy_RedirectIntoNoProxy_FollowUpGoesDirect()
        {
            AssertAsync.Run(async () =>
            {
                using var origin = new RecordingHttpServer(_ => Ok("direct"));
                using var proxy = new RecordingHttpServer(line => line.Contains("public.example/start")
                    ? Redirect(origin.Url("/landing"))
                    : Ok("proxied"));
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:" + proxy.Port);
                Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1");
                using var client = CreateClient(environmentProxy: true);

                using var response = await client.Get("http://public.example/start").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new[] { "GET http://public.example/start HTTP/1.1" }, proxy.RequestLines);
                Assert.AreEqual(new[] { "GET /landing HTTP/1.1" }, origin.RequestLines);
            });
        }

        // Case (b): only HTTP_PROXY, no http-proxy fallback for https; http:// redirects to https://.
        [Test]
        public void EnvironmentProxy_HttpToHttpsRedirectWithoutHttpsProxy_FollowUpIsNotTunnelled()
        {
            AssertAsync.Run(async () =>
            {
                // Accepts and closes: the TLS handshake of a direct follow-up fails there.
                using var httpsTarget = new RecordingHttpServer(closeOnAccept: true);
                using var proxy = new RecordingHttpServer(line => line.Contains("a.example/start")
                    ? Redirect("https://127.0.0.1:" + httpsTarget.Port + "/secure")
                    : Ok("proxied"));
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:" + proxy.Port);
                using var client = CreateClient(environmentProxy: true);

                await CaptureFailureAsync(client.Get("http://a.example/start").SendBufferedAsync());

                Assert.AreEqual(
                    new[] { "GET http://a.example/start HTTP/1.1" },
                    proxy.RequestLines,
                    "the https:// hop must not be tunnelled through HTTP_PROXY");
                Assert.GreaterOrEqual(httpsTarget.AcceptCount, 1, "the https:// hop goes direct");
            });
        }

        // Case (c): the first URI is bypassed, the redirect target is not.
        [Test]
        public void EnvironmentProxy_RedirectOutOfNoProxy_FollowUpUsesProxy()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new RecordingHttpServer(_ => Ok("proxied"));
                using var origin = new RecordingHttpServer(line => line.Contains("/start")
                    ? Redirect("http://public.example/next")
                    : Ok("direct"));
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:" + proxy.Port);
                Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1");
                using var client = CreateClient(environmentProxy: true);

                using var response = await client.Get(origin.Url("/start")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(new[] { "GET /start HTTP/1.1" }, origin.RequestLines);
                Assert.AreEqual(new[] { "GET http://public.example/next HTTP/1.1" }, proxy.RequestLines);
            });
        }

        // ------------------------------------------------------------------------------ helpers

        private static UHttpClient CreateClient(bool environmentProxy = false)
        {
            var options = new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = new RawSocketTransport(),
                DisposeTransport = true,
                FollowRedirects = true,
                // null: the environment defaults (HTTP_PROXY, HTTPS_PROXY, NO_PROXY).
                Proxy = environmentProxy ? null : new ProxySettings { UseEnvironmentVariables = false }
            };
            options.Interceptors.Add(new RedirectInterceptor());
            return new UHttpClient(options);
        }

        private static async Task<UHttpException> CaptureFailureAsync(ValueTask<UHttpResponse> send)
        {
            try
            {
                using var response = await send;
            }
            catch (UHttpException ex)
            {
                return ex;
            }

            Assert.Fail("expected a UHttpException");
            return null;
        }

        private static string Ok(string body)
        {
            return "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;
        }

        private static string Redirect(string location)
        {
            return "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }

        /// <summary>
        /// Loopback HTTP/1.1 server: one request per connection, answered by a responder that gets
        /// the request line, then closed. With <c>closeOnAccept</c> it closes every connection at once.
        /// </summary>
        private sealed class RecordingHttpServer : IDisposable
        {
            private const int MaxHeadBytes = 16 * 1024;

            private readonly TcpListener _listener;
            private readonly Func<string, string> _responder;
            private readonly bool _closeOnAccept;
            private readonly object _gate = new object();
            private readonly List<string> _requestLines = new List<string>();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private int _acceptCount;
            private bool _disposed;

            public RecordingHttpServer(Func<string, string> responder)
                : this(responder, closeOnAccept: false)
            {
            }

            public RecordingHttpServer(bool closeOnAccept)
                : this(_ => Ok(string.Empty), closeOnAccept)
            {
            }

            private RecordingHttpServer(Func<string, string> responder, bool closeOnAccept)
            {
                _responder = responder;
                _closeOnAccept = closeOnAccept;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public string[] RequestLines
            {
                get
                {
                    lock (_gate)
                        return _requestLines.ToArray();
                }
            }

            public string Url(string path) => "http://127.0.0.1:" + Port + path;

            private async Task AcceptLoopAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        lock (_gate)
                        {
                            if (_disposed)
                                return;
                        }

                        continue;
                    }

                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            client.Close();
                            return;
                        }

                        _clients.Add(client);
                    }

                    Interlocked.Increment(ref _acceptCount);
                    if (_closeOnAccept)
                    {
                        client.Close();
                        continue;
                    }

                    _ = Task.Run(() => HandleAsync(client));
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                    if (head == null)
                        return;

                    var requestLine = head.Substring(0, head.IndexOf("\r\n", StringComparison.Ordinal));
                    lock (_gate)
                        _requestLines.Add(requestLine);

                    var response = Encoding.ASCII.GetBytes(_responder(requestLine));
                    await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The client went away; the assertions read what was recorded.
                }
                finally
                {
                    client.Close();
                }
            }

            private static async Task<string> ReadHeadAsync(NetworkStream stream)
            {
                var buffer = new byte[MaxHeadBytes];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer, length, buffer.Length - length).ConfigureAwait(false);
                    if (read == 0)
                        return null;

                    length += read;
                    var text = Encoding.ASCII.GetString(buffer, 0, length);
                    if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                        return text;
                }

                return null;
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch (Exception) { }
                    }

                    _clients.Clear();
                }

                _listener.Stop();
            }
        }
    }
}
