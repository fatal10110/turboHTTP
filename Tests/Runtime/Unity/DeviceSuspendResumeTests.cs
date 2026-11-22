using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.WebSocket;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #1132 / #99 device row: the WebSocket keep-alive across real application suspensions. The peer answers
    /// every Ping late (<see cref="PongDelay"/>, inside <see cref="PongTimeout"/>), so a suspension usually
    /// starts with a Ping outstanding: on resume the client's expired pong deadline and the peer's late Pong
    /// race, which is the ordering the issue says only a device shows. The connection must stay open through
    /// every resume. Runs only under <c>Tools~/unity/run-ios-player.sh --suspend-cycles</c>, which backgrounds
    /// and foregrounds the app after <c>TURBOHTTP_SUSPEND_READY</c>; the main thread keeps yielding so the
    /// player answers its lifecycle events.
    /// </summary>
    public sealed class DeviceSuspendResumeTests
    {
        internal const int Cycles = 5;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PongDelay = TimeSpan.FromMilliseconds(1500);
        private const float RunBoundSeconds = 300f;
        private const float SettleSeconds = 4f;

        private readonly object _gate = new object();
        private readonly List<(long Ping, long Pong)> _pings = new List<(long Ping, long Pong)>();
        private volatile bool _stop;
        private readonly List<(long Start, long End)> _freezes = new List<(long Start, long End)>();
        private Task _cleanup;

        [UnityTest, Explicit("needs the run-ios-player.sh --suspend-cycles driver to background the app (#1132, #99)")]
        [Category("DeviceSuspend")]
        public IEnumerator KeepAlive_AppSuspendedWithLatePongs_StaysOpenThroughEveryResume()
        {
            WebSocketResumeListener.Install();
            var options = new WebSocketConnectionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(5),
                CloseHandshakeTimeout = TimeSpan.FromSeconds(2),
                PingInterval = PingInterval,
                PongTimeout = PongTimeout,
                IdleTimeout = TimeSpan.Zero
            };

            var peer = new InMemoryWebSocketPeer { AnswerPings = false };
            var connection = new WebSocketConnection();
            Task pump = null;
            try
            {
                yield return WaitForTask(
                    connection.ConnectAsync(new Uri("ws://localhost/suspend"), peer, options, CancellationToken.None),
                    10f, "connect");
                pump = Task.Run(() => PumpAsync(peer));
                // A background-thread heartbeat tells a frozen process from a paused player loop: the
                // player stops rendering in the background even where the OS keeps the process running.
                new Thread(Heartbeat) { IsBackground = true, Name = "suspend-heartbeat" }.Start();

                // Suspension gaps: the Stopwatch time of the last frame before and the first frame after.
                var gaps = new List<(long Start, long End)>();
                long lastResume = WebSocketConnection.ApplicationResumedTimestamp;
                long lastFrame = Stopwatch.GetTimestamp();
                float deadline = Time.realtimeSinceStartup + RunBoundSeconds;
                Debug.Log("TURBOHTTP_SUSPEND_READY cycles=" + Cycles);

                while (gaps.Count < Cycles && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    long now = Stopwatch.GetTimestamp();
                    Assert.AreEqual(WebSocketState.Open, connection.State,
                        "closed after " + gaps.Count + " resume(s): " + connection.TerminalError);

                    long resumed = WebSocketConnection.ApplicationResumedTimestamp;
                    if (resumed != lastResume)
                    {
                        lastResume = resumed;
                        gaps.Add((lastFrame, now));
                        Debug.Log("TURBOHTTP_SUSPEND_RESUMED n=" + gaps.Count + " gap=" + Seconds(now - lastFrame) + "s");
                    }

                    lastFrame = now;
                }

                Assert.AreEqual(Cycles, gaps.Count, "the driver did not suspend and resume the app " + Cycles + " times");

                // Give every expired deadline time to fire after the last resume.
                float settle = Time.realtimeSinceStartup + SettleSeconds;
                while (Time.realtimeSinceStartup < settle)
                {
                    yield return null;
                    Assert.AreEqual(WebSocketState.Open, connection.State,
                        "closed after the last resume: " + connection.TerminalError);
                }

                yield return WaitForTask(connection.SendTextAsync("after-resume", CancellationToken.None), 10f, "send");
                var echoed = peer.ReadFrameAsync(TimeSpan.FromSeconds(10));
                yield return WaitForTask(echoed, 15f, "peer read");

                int outstanding = 0;
                double longest = 0;
                int freezes;
                lock (_gate)
                {
                    freezes = _freezes.Count;
                    foreach (var freeze in _freezes)
                    {
                        longest = Math.Max(longest, Seconds(freeze.End - freeze.Start));
                        foreach (var ping in _pings)
                        {
                            if (ping.Ping < freeze.Start && (ping.Pong == 0 || ping.Pong > freeze.End))
                            {
                                outstanding++;
                                break;
                            }
                        }
                    }
                }

                Debug.Log("TURBOHTTP_SUSPEND_RESULT resumes=" + gaps.Count + " process-freezes=" + freezes
                    + " with-ping-outstanding=" + outstanding + " longest-freeze=" + longest.ToString("F1")
                    + "s grace-waits=" + connection.KeepAlivePongGraceWaits
                    + " liveness-extensions=" + connection.KeepAliveLivenessExtensions);
                Assert.Greater(longest, PongTimeout.TotalSeconds,
                    "the process never froze for longer than the pong timeout in the background (the OS kept it running)");
                Assert.GreaterOrEqual(outstanding, 1, "no process freeze started with a Ping outstanding");
            }
            finally
            {
                // An iterator cannot yield in finally: UnityTearDown awaits the cleanup.
                _stop = true;
                _cleanup = CleanupAsync(connection, peer, pump);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_cleanup != null)
                yield return WaitForTask(_cleanup, 15f, "cleanup");
        }

        private static async Task CleanupAsync(WebSocketConnection connection, InMemoryWebSocketPeer peer, Task pump)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            peer.Dispose();
            if (pump != null)
                await pump.ConfigureAwait(false);
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

        // Reads the client's frames; answers each Ping after PongDelay and records when.
        private async Task PumpAsync(InMemoryWebSocketPeer peer)
        {
            var replies = new List<Task>();
            while (!_stop)
            {
                InMemoryWebSocketPeer.ReceivedFrame frame;
                try
                {
                    frame = await peer.ReadFrameAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (frame.Opcode != WebSocketOpcode.Ping)
                    continue;

                int index;
                lock (_gate)
                {
                    index = _pings.Count;
                    _pings.Add((Stopwatch.GetTimestamp(), 0));
                }

                replies.Add(ReplyLateAsync(peer, frame.Payload, index));
            }

            await Task.WhenAll(replies).ConfigureAwait(false);
        }

        private async Task ReplyLateAsync(InMemoryWebSocketPeer peer, byte[] payload, int index)
        {
            await Task.Delay(PongDelay).ConfigureAwait(false);
            if (_stop)
                return;
            try
            {
                await peer.SendFrameAsync(WebSocketOpcode.Pong, payload, fin: true).ConfigureAwait(false);
            }
            catch (Exception) when (_stop)
            {
                return;
            }

            lock (_gate)
                _pings[index] = (_pings[index].Ping, Stopwatch.GetTimestamp());
        }

        private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            yield return new WaitUntil(() => task.IsCompleted || Time.realtimeSinceStartup > deadline);

            Assert.IsTrue(task.IsCompleted, "Timed out waiting for " + what + ".");
            if (task.IsFaulted)
                Assert.Fail(what + " failed: " + task.Exception?.GetBaseException());
        }
    }
}
