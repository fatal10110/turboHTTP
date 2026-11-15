using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #830/#831: a reused HTTP/1.1 CONNECT tunnel that stays silent after an idempotent request gets the
    /// same first-byte bound as a direct connection, and the request is retried once on a new proxy
    /// connection with a new tunnel.
    /// </summary>
    public partial class RawSocketTransportProxyTunnelTests
    {
        [Test]
        public void HttpsViaProxy_SilentReusedTunnel_IdempotentGet_IsRetriedOnNewTunnel()
        {
            AssertAsync.Run(async () =>
            {
                TestRequirements.RequireBouncyCastle();

                using var certificate = TryCreateLocalhostCertificate();
                if (certificate == null)
                    TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");

                var secondSentOnReusedTunnel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseSilentTunnel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    using var originServer = new HttpsOriginServer(certificate, async (stream, index) =>
                    {
                        var request = await ReadRequestStartAndHeadersAsync(stream, CancellationToken.None);
                        if (index == 1)
                        {
                            await WriteResponseAsync(
                                stream,
                                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\nfirst");

                            // The tunnel goes silent: it reads the next request and never answers.
                            var followUp = await TryReadRequestStartAndHeadersAsync(stream, TimeSpan.FromSeconds(10));
                            if (followUp != null)
                                secondSentOnReusedTunnel.TrySetResult(true);
                            await releaseSilentTunnel.Task;
                            return;
                        }

                        if (request.RequestLine != "GET /second HTTP/1.1")
                            return; // unexpected: no answer, so the GET below fails
                        await WriteResponseAsync(
                            stream,
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    });

                    using var proxyServer = new ConnectProxyServer();
                    var pool = new TcpConnectionPool(
                        tlsBackend: TlsBackend.BouncyCastle,
                        reusedConnectionFirstByteTimeout: TimeSpan.FromMilliseconds(200));
                    using var client = new UHttpClient(new UHttpClientOptions
                    {
                        AllowCleartext = true,
                        Transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle),
                        DisposeTransport = true,
                        Proxy = new ProxySettings
                        {
                            Address = new Uri($"http://127.0.0.1:{proxyServer.Port}"),
                            UseEnvironmentVariables = false
                        }
                    });

                    using (var first = await client.Get($"https://localhost:{originServer.Port}/first").SendBufferedAsync())
                        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

                    // Before #831 the reused tunnel had no first-byte bound and the GET waited for this timeout.
                    using var second = await client.Get($"https://localhost:{originServer.Port}/second")
                        .WithTimeout(TimeSpan.FromSeconds(5))
                        .SendBufferedAsync();

                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                    Assert.AreEqual("ok", second.GetBodyAsString());
                    var sentOnReusedTunnel = await Task.WhenAny(secondSentOnReusedTunnel.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(secondSentOnReusedTunnel.Task, sentOnReusedTunnel,
                        "the first attempt of /second must go out on the reused tunnel");
                    Assert.AreEqual(2, proxyServer.AcceptCount, "the retry opens a new proxy connection");
                    Assert.AreEqual(2, originServer.AcceptCount, "the retry establishes a new tunnel");
                }
                finally
                {
                    releaseSilentTunnel.TrySetResult(true);
                }
            }, TimeSpan.FromSeconds(30));
        }
    }
}
