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
    /// #1289 / #99 device row: real sockets across real application suspensions, against public servers with
    /// BouncyCastle (ALPN h2; PING every second, 2 s timeout) and one wss connection (keep-alive Ping every
    /// second, 2 s pong timeout). Each cycle holds a large HTTP/2 download open, unread, across the suspension,
    /// so that connection's liveness PINGs are armed while the process is frozen. After each resume: the held
    /// download must deliver more data or fail fast as <see cref="UHttpException"/>; the first GET on the idle
    /// pooled connection (reuse probe: one PING first) must succeed, or fail fast and succeed on the retry; the
    /// wss connection must echo, or leave Open within its keep-alive deadline and echo again after a reconnect.
    /// "Fast" means not a timeout and within <see cref="FailFastBound"/>. Every suspension must freeze the process
    /// past the keep-alive deadlines. Runs only under <c>Tools~/unity/run-ios-player.sh --suspend-cycles</c> (build
    /// with <c>--filter DeviceRealSocketSuspendResumeTests --integration</c>), which reads the cycle count,
    /// background time and gap from <c>TURBOHTTP_SUSPEND_READY</c>.
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
        private const string HeldHost = "dl.google.com";
        // A large public h2 download: held open unread across each suspension, then read again.
        private static readonly Uri HeldUrl = new Uri("https://" + HeldHost + "/chrome/mac/universal/stable/GGRO/googlechrome.dmg");
        private const int DrainBytes = 4 * 1024 * 1024;
        private const float DrainBoundSeconds = 30f;
        private static readonly TimeSpan HeldTimeout = TimeSpan.FromSeconds(180);
        // A request that fails after a resume must end well inside its timeout: the keep-alive deadlines plus a margin.
        private static readonly TimeSpan FailFastBound = TimeSpan.FromSeconds(10);
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
            // Liveness PINGs run only while a stream is open, so each cycle holds a large download open (unread,
            // flow-controlled) across the suspension; the reuse probe makes the first request on an idle pooled
            // connection after a resume send a PING first.
            var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle, new Http2Options
            {
                PingInterval = KeepAlive,
                PingTimeout = KeepAliveTimeout,
                ReuseProbeIdleThreshold = KeepAlive
            }, new StreamingOptions
            {
                // The held body sits unread for about 50 s per cycle on a monotonic clock that runs through the
                // freeze; the default 60 s stall abort would end it for the wrong reason.
                Http2StallTimeoutSeconds = 600
            });
            var http = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = false });
            WebSocketClient ws = null;
            var receivers = new List<Task>();
            Receiver receiver = null;
            UHttpStreamingResponse held = null;
            Holder hold = null;
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

                hold = new Holder();
                yield return WaitForTask(Task.Run(() => OpenHeldAsync(http, hold)), (float)RequestBound.TotalSeconds + 5f, "held download");
                held = hold.Response;
                Assert.IsTrue(transport.HasHttp2Connection(HeldHost, 443),
                    "BouncyCastle did not open an HTTP/2 connection to " + HeldHost + " (ALPN h2)");

                new Thread(Heartbeat) { IsBackground = true, Name = "suspend-heartbeat" }.Start();
                long lastResume = WebSocketConnection.ApplicationResumedTimestamp;
                int resumes = 0, httpRetries = 0, wsReconnects = 0, heldFailures = 0;
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

                    // HTTP/2 liveness: the stream held open across the freeze keeps its connection's PING loop armed.
                    // After the resume it must deliver more than the flow-control windows buffered before the freeze,
                    // or fail fast as UHttpException (the connection presumed dead), never hang.
                    var drain = new Outcome();
                    yield return WaitForTask(Task.Run(() => DrainAsync(held, drain)), DrainBoundSeconds + 5f,
                        "held download after resume " + resumes);
                    string heldResult = drain.Failure == null ? "ok" : Describe(drain.Failure);
                    if (drain.Failure != null)
                    {
                        AssertFailedFast(drain, "held download after resume " + resumes);
                        heldFailures++;
                    }

                    held.Dispose();
                    held = null;
                    hold = null;

                    // HTTP/2 reuse: the first request on the idle pooled connection sends the reuse PING first; it
                    // succeeds, or fails fast and the retry succeeds.
                    bool pooled = transport.HasHttp2Connection(HttpHost, 443);
                    var get = new Outcome();
                    yield return WaitForTask(Task.Run(() => GetAsync(http, get)), (float)RequestBound.TotalSeconds + 5f,
                        "GET after resume " + resumes);
                    string httpResult = get.Failure == null ? "ok" : Describe(get.Failure);
                    if (get.Failure != null)
                    {
                        AssertFailedFast(get, "GET after resume " + resumes);
                        httpRetries++;
                        var retry = new Outcome();
                        yield return WaitForTask(Task.Run(() => GetAsync(http, retry)), (float)RequestBound.TotalSeconds + 5f,
                            "GET retry after resume " + resumes);
                        Assert.IsNull(retry.Failure, "GET retry after resume " + resumes + ": " + retry.Failure);
                        httpResult += ",retry-ok-" + retry.Milliseconds + "ms";
                    }

                    // wss: the same connection echoes; otherwise it must leave Open within its keep-alive deadline
                    // (no hang), and a new connection must echo.
                    string wsState = ws.State.ToString();
                    string wsResult = "echo-ok";
                    bool echoed = false;
                    if (ws.State == WebSocketState.Open)
                    {
                        string expected = "resume-" + resumes;
                        var send = ws.SendAsync(expected, CancellationToken.None);
                        yield return WaitForTask(send, 10f, "wss send after resume " + resumes, allowFault: true);
                        if (!send.IsFaulted)
                        {
                            float echoDeadline = Time.realtimeSinceStartup + EchoBoundSeconds;
                            yield return new WaitUntil(() => HasEcho(expected) || ws.State != WebSocketState.Open
                                || Time.realtimeSinceStartup > echoDeadline);
                            echoed = HasEcho(expected);
                        }
                    }

                    if (!echoed)
                    {
                        float closeDeadline = Time.realtimeSinceStartup + (float)(KeepAlive + KeepAliveTimeout).TotalSeconds + 5f;
                        yield return new WaitUntil(() => ws.State != WebSocketState.Open || Time.realtimeSinceStartup > closeDeadline);
                        Assert.AreNotEqual(WebSocketState.Open, ws.State,
                            "wss after resume " + resumes + " neither echoed nor closed within its keep-alive deadline");
                        var terminal = receiver.Failure;
                        wsResult = "closed:" + (terminal == null ? "none" : terminal.GetBaseException().GetType().Name
                            + (terminal is WebSocketException wse ? "/" + wse.Error : "")) + ",reconnect";
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

                    Debug.Log("TURBOHTTP_SUSPEND_RESUMED n=" + resumes + " h2-held=" + heldResult + " held-ms=" + drain.Milliseconds
                        + " h2-pooled=" + pooled + " http=" + httpResult + " http-ms=" + get.Milliseconds + " wss-state=" + wsState + " wss=" + wsResult);

                    if (resumes < Cycles)
                    {
                        hold = new Holder();
                        yield return WaitForTask(Task.Run(() => OpenHeldAsync(http, hold)), (float)RequestBound.TotalSeconds + 5f,
                            "held download " + (resumes + 1));
                        held = hold.Response;
                    }
                }

                Assert.AreEqual(Cycles, resumes, "the driver did not suspend and resume the app " + Cycles + " times");

                double longest = 0;
                int freezes, longFreezes = 0;
                double deadlines = (KeepAlive + KeepAliveTimeout).TotalSeconds;
                lock (_gate)
                {
                    freezes = _freezes.Count;
                    foreach (var freeze in _freezes)
                    {
                        double seconds = Seconds(freeze.End - freeze.Start);
                        longest = Math.Max(longest, seconds);
                        if (seconds > deadlines)
                            longFreezes++;
                    }
                }

                Debug.Log("TURBOHTTP_SUSPEND_RESULT resumes=" + resumes + " process-freezes=" + freezes
                    + " past-deadlines=" + longFreezes + " longest-freeze=" + longest.ToString("F1")
                    + "s held-failures=" + heldFailures + " http-retries=" + httpRetries + " wss-reconnects=" + wsReconnects);
                Assert.GreaterOrEqual(longFreezes, Cycles,
                    "not every suspension froze the process past the keep-alive deadlines (the OS kept it running)");
            }
            finally
            {
                // An iterator cannot yield in finally: UnityTearDown awaits the cleanup.
                _stop = true;
                // A held download whose open failed after the response arrived has no other owner.
                held?.Dispose();
                if (hold?.Response != null && !ReferenceEquals(hold.Response, held))
                    hold.Response.Dispose();
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

        private static async Task OpenHeldAsync(UHttpClient client, Holder holder)
        {
            var response = await client.Get(HeldUrl.AbsoluteUri).WithTimeout(HeldTimeout).SendStreamingAsync()
                .ConfigureAwait(false);
            holder.Response = response;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "held download status");
            var buffer = new byte[16 * 1024];
            Assert.Greater(await response.Body.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false), 0,
                "held download sent no body");
        }

        private static async Task DrainAsync(UHttpStreamingResponse response, Outcome outcome)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (total < DrainBytes)
                {
                    int read = await response.Body.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                        throw new InvalidOperationException("held download ended after " + total + " bytes");
                    total += read;
                }
            }
            catch (Exception ex)
            {
                outcome.Failure = ex;
            }

            outcome.Milliseconds = clock.ElapsedMilliseconds;
        }

        // A failure after a resume is allowed only as a fast UHttpException, never a request-timeout hang.
        private static void AssertFailedFast(Outcome outcome, string what)
        {
            Assert.IsInstanceOf<UHttpException>(outcome.Failure, what);
            Assert.AreNotEqual(UHttpErrorType.Timeout, ((UHttpException)outcome.Failure).HttpError.Type,
                what + " hung until its timeout: " + outcome.Failure);
            Assert.Less(outcome.Milliseconds, FailFastBound.TotalMilliseconds,
                what + " took " + outcome.Milliseconds + " ms to fail: " + outcome.Failure);
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

        private sealed class Holder
        {
            public volatile UHttpStreamingResponse Response;
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
