#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #1289 / #99 device row: real sockets across real application suspensions. One HTTP/2 connection
    /// (BouncyCastle, ALPN h2, PING every second with a 2 s timeout) and one wss connection (keep-alive Ping
    /// every second, 2 s pong timeout) to public servers stay in their pools while the app is backgrounded for
    /// longer than every keep-alive deadline. After each resume the next request on the pooled HTTP/2
    /// connection must succeed, or fail fast as <see cref="UHttpException"/> and succeed on the retry (no hang
    /// until the request timeout). The wss connection must still echo, or have closed with a
    /// <see cref="WebSocketException"/> and echo again after a reconnect (never a hang). Runs only under
    /// <c>Tools~/unity/run-ios-player.sh --suspend-cycles</c> (build with <c>--filter DeviceRealSocketSuspendResumeTests
    /// --integration</c>), which reads the cycle count, background time and gap from <c>TURBOHTTP_SUSPEND_READY</c>.
    /// </summary>
    [Category("Integration")]
    [Category("ExternalNetwork")]
    public sealed class DeviceRealSocketSuspendResumeTests
    {
        private const int Cycles = 4;
        private const int BackgroundSeconds = 30;
        private const int GapSeconds = 20;
        private const string HttpHost = "www.google.com";
        private static readonly Uri HttpUrl = new Uri("https://" + HttpHost + "/");
        private static readonly Uri EchoUrl = new Uri("wss://echo.websocket.org/");
        private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RequestBound = TimeSpan.FromSeconds(15);
        private const float EchoBoundSeconds = 15f;
        private const float RunBoundSeconds = 900f;

        private readonly object _gate = new object();
        private readonly List<(long Start, long End)> _freezes = new List<(long Start, long End)>();
        private readonly ConcurrentQueue<string> _echoes = new ConcurrentQueue<string>();
        private volatile bool _stop;
        private Task _cleanup;

        [UnityTest, Explicit("needs the run-ios-player.sh --suspend-cycles driver and public servers (#1289, #99)")]
        [Category("DeviceSuspend")]
        [Timeout(1200000)] // four 30 s suspensions plus the probes; UnityTest's default is 180 s
        public IEnumerator PooledHttp2AndWss_AppSuspendedPastKeepAliveDeadlines_RecoverOnEveryResume()
        {
            RawSocketTransport.EnsureRegistered();
            RawSocketWebSocketTransport.EnsureRegistered();
            WebSocketResumeListener.Install();

            var pool = new TcpConnectionPool(tlsBackend: TlsBackend.BouncyCastle);
            var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle,
                new Http2Options { PingInterval = KeepAlive, PingTimeout = KeepAliveTimeout });
            var http = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = false });
            WebSocketClient ws = null;
            var receivers = new List<Task>();
            Receiver receiver = null;
            try
            {
                var first = new Outcome();
                yield return WaitForTask(Task.Run(() => GetAsync(http, first)), (float)RequestBound.TotalSeconds + 5f, "first GET");
                Assert.IsNull(first.Failure, "first GET: " + first.Failure);
                Assert.IsTrue(transport.HasHttp2Connection(HttpHost, 443),
                    "BouncyCastle did not open an HTTP/2 connection to " + HttpHost + " (ALPN h2)");

                ws = new WebSocketClient();
                yield return WaitForTask(ws.ConnectAsync(EchoUrl, WsOptions(), CancellationToken.None), 20f, "wss connect");
                receiver = new Receiver();
                var firstWs = ws;
                var firstReceiver = receiver;
                receivers.Add(Task.Run(() => ReceiveLoopAsync(firstWs, firstReceiver)));
                yield return Echo(ws, "before-suspend");

                new Thread(Heartbeat) { IsBackground = true, Name = "suspend-heartbeat" }.Start();
                long lastResume = WebSocketConnection.ApplicationResumedTimestamp;
                int resumes = 0, httpRetries = 0, wsReconnects = 0, h2Kept = 0;
                float deadline = Time.realtimeSinceStartup + RunBoundSeconds;
                Debug.Log("TURBOHTTP_SUSPEND_READY cycles=" + Cycles + " bg=" + BackgroundSeconds + " gap=" + GapSeconds);

                while (resumes < Cycles && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    long resumed = WebSocketConnection.ApplicationResumedTimestamp;
                    if (resumed == lastResume)
                        continue;
                    lastResume = resumed;
                    resumes++;

                    // HTTP/2: the pooled connection (or its replacement after a failed PING) serves the next request.
                    bool kept = transport.HasHttp2Connection(HttpHost, 443);
                    if (kept)
                        h2Kept++;
                    var get = new Outcome();
                    yield return WaitForTask(Task.Run(() => GetAsync(http, get)), (float)RequestBound.TotalSeconds + 5f,
                        "GET after resume " + resumes);
                    string httpResult = get.Failure == null ? "ok" : Describe(get.Failure);
                    if (get.Failure != null)
                    {
                        Assert.IsInstanceOf<UHttpException>(get.Failure, "GET after resume " + resumes);
                        httpRetries++;
                        var retry = new Outcome();
                        yield return WaitForTask(Task.Run(() => GetAsync(http, retry)), (float)RequestBound.TotalSeconds + 5f,
                            "GET retry after resume " + resumes);
                        Assert.IsNull(retry.Failure, "GET retry after resume " + resumes + ": " + retry.Failure);
                        httpResult += ",retry-ok-" + retry.Milliseconds + "ms";
                    }

                    // wss: the same connection echoes, or it closed with a WebSocketException and a new one echoes.
                    string wsState = ws.State.ToString();
                    string wsResult = "echo-ok";
                    if (ws.State == WebSocketState.Open)
                    {
                        var send = ws.SendAsync("resume-" + resumes, CancellationToken.None);
                        yield return WaitForTask(send, 10f, "wss send after resume " + resumes, allowFault: true);
                        if (send.IsFaulted)
                        {
                            Assert.IsInstanceOf<WebSocketException>(send.Exception?.GetBaseException(),
                                "wss send after resume " + resumes);
                        }
                        else
                        {
                            // A connection that died in the background ends with its keep-alive deadline instead.
                            string expected = "resume-" + resumes;
                            float echoDeadline = Time.realtimeSinceStartup + EchoBoundSeconds;
                            yield return new WaitUntil(() => HasEcho(expected) || ws.State != WebSocketState.Open
                                || Time.realtimeSinceStartup > echoDeadline);
                            if (ws.State == WebSocketState.Open)
                                Assert.IsTrue(HasEcho(expected), "open wss did not echo '" + expected + "' within " + EchoBoundSeconds + " s");
                        }
                    }

                    if (ws.State != WebSocketState.Open)
                    {
                        var terminal = receiver.Failure;
                        Assert.IsTrue(terminal == null || terminal is WebSocketException,
                            "wss closed after resume " + resumes + " with " + terminal);
                        wsResult = "closed:" + (terminal is WebSocketException wse ? wse.Error.ToString() : "clean") + ",reconnect";
                        wsReconnects++;
                        ws.Dispose();
                        ws = new WebSocketClient();
                        yield return WaitForTask(ws.ConnectAsync(EchoUrl, WsOptions(), CancellationToken.None), 20f,
                            "wss reconnect after resume " + resumes);
                        receiver = new Receiver();
                        var nextWs = ws;
                        var nextReceiver = receiver;
                        receivers.Add(Task.Run(() => ReceiveLoopAsync(nextWs, nextReceiver)));
                        yield return Echo(ws, "reconnected-" + resumes);
                    }

                    Debug.Log("TURBOHTTP_SUSPEND_RESUMED n=" + resumes + " h2-pooled=" + kept + " http=" + httpResult
                        + " http-ms=" + get.Milliseconds + " wss-state=" + wsState + " wss=" + wsResult);
                }

                Assert.AreEqual(Cycles, resumes, "the driver did not suspend and resume the app " + Cycles + " times");

                double longest = 0;
                int freezes;
                lock (_gate)
                {
                    freezes = _freezes.Count;
                    foreach (var freeze in _freezes)
                        longest = Math.Max(longest, Seconds(freeze.End - freeze.Start));
                }

                Debug.Log("TURBOHTTP_SUSPEND_RESULT resumes=" + resumes + " process-freezes=" + freezes
                    + " longest-freeze=" + longest.ToString("F1") + "s h2-pooled-at-resume=" + h2Kept
                    + " http-retries=" + httpRetries + " wss-reconnects=" + wsReconnects);
                Assert.Greater(longest, KeepAliveTimeout.TotalSeconds + KeepAlive.TotalSeconds,
                    "the process never froze past the keep-alive deadlines in the background (the OS kept it running)");
            }
            finally
            {
                // An iterator cannot yield in finally: UnityTearDown awaits the cleanup.
                _stop = true;
                _cleanup = CleanupAsync(http, transport, pool, ws, receivers);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_cleanup != null)
                yield return WaitForTask(_cleanup, 20f, "cleanup");
        }

        private static WebSocketConnectionOptions WsOptions() => new WebSocketConnectionOptions
        {
            HandshakeTimeout = TimeSpan.FromSeconds(15),
            CloseHandshakeTimeout = TimeSpan.FromSeconds(2),
            PingInterval = KeepAlive,
            PongTimeout = KeepAliveTimeout,
            IdleTimeout = TimeSpan.Zero
        };

        private IEnumerator Echo(WebSocketClient ws, string text)
        {
            yield return WaitForTask(ws.SendAsync(text, CancellationToken.None), 10f, "wss send " + text);
            float deadline = Time.realtimeSinceStartup + EchoBoundSeconds;
            yield return new WaitUntil(() => HasEcho(text) || Time.realtimeSinceStartup > deadline);
            Assert.IsTrue(HasEcho(text), "no echo of '" + text + "' within " + EchoBoundSeconds + " s (state " + ws.State + ")");
        }

        private bool HasEcho(string text)
        {
            foreach (var echo in _echoes)
            {
                if (echo == text)
                    return true;
            }

            return false;
        }

        // Records every text message (the echo server also sends a greeting) until the connection ends.
        private async Task ReceiveLoopAsync(WebSocketClient ws, Receiver receiver)
        {
            while (!_stop)
            {
                try
                {
                    using var message = await ws.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                    if (message.IsText)
                        _echoes.Enqueue(message.Text);
                }
                catch (Exception ex)
                {
                    receiver.Failure = ex; // the main loop checks State and this failure's type
                    return;
                }
            }
        }

        private static async Task GetAsync(UHttpClient client, Outcome outcome)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await client.Get(HttpUrl.AbsoluteUri).WithTimeout(RequestBound).SendBufferedAsync()
                    .ConfigureAwait(false);
                outcome.Status = response.StatusCode;
                if (response.StatusCode != HttpStatusCode.OK)
                    outcome.Failure = new InvalidOperationException("status " + (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                outcome.Failure = ex;
            }

            outcome.Milliseconds = clock.ElapsedMilliseconds;
        }

        private static async Task CleanupAsync(UHttpClient http, RawSocketTransport transport, TcpConnectionPool pool,
            WebSocketClient ws, List<Task> receivers)
        {
            try
            {
                if (ws != null)
                    await ws.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                http.Dispose();
                transport.Dispose();
                pool.Dispose();
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
        }

        // Records every stretch of more than a second in which this thread did not run: a process freeze.
        private void Heartbeat()
        {
            long last = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                Thread.Sleep(50);
                long now = Stopwatch.GetTimestamp();
                if (now - last > Stopwatch.Frequency)
                {
                    lock (_gate)
                        _freezes.Add((last, now));
                }

                last = now;
            }
        }

        private static string Describe(Exception ex) =>
            ex is UHttpException http ? "UHttpException:" + http.HttpError.Type : ex.GetType().Name;

        private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds, string what, bool allowFault = false)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            yield return new WaitUntil(() => task.IsCompleted || Time.realtimeSinceStartup > deadline);

            Assert.IsTrue(task.IsCompleted, "Timed out waiting for " + what + ".");
            if (task.IsFaulted && !allowFault)
                Assert.Fail(what + " failed: " + task.Exception?.GetBaseException());
        }

        private sealed class Receiver
        {
            public volatile Exception Failure;
        }

        private sealed class Outcome
        {
            public HttpStatusCode Status;
            public Exception Failure;
            public long Milliseconds;
        }
    }
}
#endif
