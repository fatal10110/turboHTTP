using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Measures, on the runtime the tests run on, the two socket-layer facts the SAEA close order
    /// rests on (#843), and checks the build's decision
    /// (<see cref="SaeaPlatformDetection.CloseAbortsPendingIo"/>) against them on Windows:
    /// <list type="bullet">
    ///   <item>Does a local shutdown end a pending receive while the descriptor is open? Shutting
    ///   down first and holding the descriptor (#384) needs it, else the abandoned read is held to
    ///   its bound (what #405 measured in the Windows Editor).</item>
    ///   <item>Does a close under a pending receive end it, and does a new socket that gets the
    ///   closed handle value still receive? Closing first needs both: IL2CPP's selector keys its
    ///   jobs by handle and its close does not remove them (#384), so a stale job can swallow the
    ///   new socket's receive.</item>
    /// </list>
    /// Each test writes a <c>PROBE</c> line with the runtime and the measured outcome into the
    /// result XML. The receives use both async paths of the runtime's socket layer: SAEA
    /// (<c>SocketIoMode.Saea</c>) and Begin/EndReceive (what <c>NetworkStream</c> reads with).
    /// </summary>
    [TestFixture]
    public class SocketCloseOrderProbeTests
    {
        private const int Saea = 0;
        private const int Apm = 1;

        // How long a receive gets to end after the step that should end it. An ended receive ends
        // within milliseconds; this only bounds the wait for one that does not end at all.
        private static readonly TimeSpan EndBound = TimeSpan.FromSeconds(3);

        // New sockets created after a close while looking for one that got the closed handle value.
        private const int ReuseAttempts = 32;

        // Closes under a pending receive per test: a stale job shows only where a value is reused.
        private const int CloseCycles = 10;

        [TestCase(Saea)]
        [TestCase(Apm)]
        public void Shutdown_PendingReceive_EndsWhileTheDescriptorIsOpen(int api)
        {
            AssertAsync.Run(async () =>
            {
                using var pair = await SocketChannelLifecycleTests.LoopbackPair.CreateAsync();
                var receive = StartReceive(pair.Client, api);
                Assert.IsFalse(receive.IsCompleted, "nothing was sent: the receive is pending");

                pair.Client.Shutdown(SocketShutdown.Both);
                bool endedOnShutdown = await EndsWithinAsync(receive, EndBound);
                string onShutdown = endedOnShutdown ? receive.Result.ToString() : "pending";

                // A receive the local shutdown left pending: does the peer's close end it?
                string onPeerClose = "-";
                if (!endedOnShutdown)
                {
                    pair.Server.Shutdown(SocketShutdown.Both);
                    onPeerClose = await EndsWithinAsync(receive, EndBound) ? receive.Result.ToString() : "pending";
                }

                Record($"shutdown api={ApiName(api)} endedOnShutdown={endedOnShutdown} result={onShutdown} afterPeerClose={onPeerClose}");

                if (SaeaPlatformDetection.CurrentOs == SocketOsFamily.Windows && !SaeaPlatformDetection.CloseAbortsPendingIo)
                {
                    Assert.IsTrue(endedOnShutdown,
                        "this runtime shuts down first and holds the descriptor, but a shutdown does not end a pending receive: " +
                        "every abandoned read would be held to its bound (#405, #843)");
                }
            }, Guard);
        }

        [TestCase(Saea)]
        [TestCase(Apm)]
        public void Close_PendingReceive_EndsItAndANewSocketWithTheSameHandleReceives(int api)
        {
            if (!SaeaPlatformDetection.CloseAbortsPendingIo)
            {
                // A runtime that shuts down first is one where a close under a pending receive can
                // leave a stale selector job (#384): the probe would leave one for the next tests.
                // Not an Ignore: the skip baselines of the player suites count every skipped test.
                Record($"close api={ApiName(api)} not probed: this runtime shuts down first");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int ended = 0, reused = 0, received = 0, reusedReceived = 0;
                    var results = new SortedSet<string>(StringComparer.Ordinal);
                    for (int cycle = 0; cycle < CloseCycles; cycle++)
                    {
                        var outcome = await CloseUnderPendingReceiveAsync(listener, api);
                        ended += outcome.EndedOnClose ? 1 : 0;
                        reused += outcome.HandleReused ? 1 : 0;
                        received += outcome.NewSocketReceived ? 1 : 0;
                        reusedReceived += outcome.HandleReused && outcome.NewSocketReceived ? 1 : 0;
                        results.Add($"close:{outcome.OnClose} new:{outcome.OnNewReceive}");
                    }

                    Record($"close api={ApiName(api)} cycles={CloseCycles} endedOnClose={ended} handleReused={reused} " +
                           $"newSocketReceived={received} reusedHandleReceived={reusedReceived} results=[{string.Join("; ", results)}]");

                    if (SaeaPlatformDetection.CurrentOs == SocketOsFamily.Windows && SaeaPlatformDetection.CloseAbortsPendingIo)
                    {
                        Assert.AreEqual(CloseCycles, ended, "this runtime closes first, but a close did not end the pending receive");
                        Assert.AreEqual(reused, reusedReceived,
                            "this runtime closes first, but a new socket with the closed handle value lost its receive (stale selector job, #384, #843)");
                        Assert.AreEqual(CloseCycles, received, "this runtime closes first, but a new socket lost its receive after a close under a pending receive");
                    }
                }
                finally
                {
                    listener.Stop();
                }
            }, Guard);
        }

        /// <summary>
        /// Closes a socket under a pending receive, then opens new sockets until one gets the closed
        /// handle value, and has that one (or, without reuse, the first new one) receive one byte.
        /// </summary>
        private static async Task<CloseOutcome> CloseUnderPendingReceiveAsync(TcpListener listener, int api)
        {
            var open = new List<Socket>();
            try
            {
                var (client, server) = await ConnectAsync(listener);
                open.Add(server);
                long closedHandle = client.Handle.ToInt64();
                var abandoned = StartReceive(client, api);
                Assert.IsFalse(abandoned.IsCompleted, "nothing was sent: the receive is pending");

                client.Close();
                var outcome = new CloseOutcome { EndedOnClose = await EndsWithinAsync(abandoned, EndBound) };
                outcome.OnClose = outcome.EndedOnClose ? abandoned.Result.ToString() : "pending";

                // The new sockets stay open until the cycle ends, so a value they took is not handed out again.
                Socket target = null, targetPeer = null;
                for (int attempt = 0; attempt < ReuseAttempts && !outcome.HandleReused; attempt++)
                {
                    var (c, s) = await ConnectAsync(listener);
                    open.Add(c);
                    open.Add(s);
                    if (target == null)
                    {
                        target = c;
                        targetPeer = s;
                    }

                    if (c.Handle.ToInt64() == closedHandle || s.Handle.ToInt64() == closedHandle)
                    {
                        outcome.HandleReused = true;
                        target = c.Handle.ToInt64() == closedHandle ? c : s;
                        targetPeer = target == c ? s : c;
                    }
                }

                var next = StartReceive(target, api);
                targetPeer.Send(new byte[] { 1 });
                bool ended = await EndsWithinAsync(next, EndBound);
                outcome.NewSocketReceived = ended && next.Result.Error == SocketError.Success && next.Result.Bytes == 1;
                outcome.OnNewReceive = ended ? next.Result.ToString() : "pending";
                return outcome;
            }
            finally
            {
                foreach (var socket in open)
                {
                    try { socket.Dispose(); } catch { }
                }
            }
        }

        // Guard only: every awaited step is bounded by EndBound long before this.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(120);

        private static void Record(string outcome)
        {
            TestContext.WriteLine(
                $"PROBE #843 os={SaeaPlatformDetection.CurrentOs} runtime={SaeaPlatformDetection.CurrentRuntime} " +
                $"closeFirst={SaeaPlatformDetection.CloseAbortsPendingIo} holdsPendingIo={SocketHandleHold.HoldsPendingIo} " +
                $"shutdownBeforeClose={SaeaSocketChannel.ShutdownBeforeClose} {outcome}");
        }

        private static string ApiName(int api) => api == Saea ? "SAEA" : "BeginReceive";

        private static async Task<bool> EndsWithinAsync(Task task, TimeSpan bound)
        {
            if (task.IsCompleted)
                return true;
            return await Task.WhenAny(task, Task.Delay(bound)) == task;
        }

        private static async Task<(Socket client, Socket server)> ConnectAsync(TcpListener listener)
        {
            var accept = listener.AcceptSocketAsync();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return (client, await accept);
        }

        /// <summary>Starts one receive through <paramref name="api"/>; the task never faults.</summary>
        private static Task<ReceiveOutcome> StartReceive(Socket socket, int api)
        {
            var done = new TaskCompletionSource<ReceiveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var buffer = new byte[16];
            try
            {
                if (api == Saea)
                {
                    // The args outlive this method while the receive is pending; whoever reads
                    // the result (the Completed handler, or this frame on a synchronous
                    // completion or a throw) disposes them once.
                    var args = new SocketAsyncEventArgs();
                    bool pending = false;
                    try
                    {
                        args.SetBuffer(buffer, 0, buffer.Length);
                        args.Completed += (_, e) =>
                        {
                            done.TrySetResult(new ReceiveOutcome(e.SocketError, e.BytesTransferred, null));
                            e.Dispose();
                        };
                        pending = socket.ReceiveAsync(args);
                        if (!pending)
                            done.TrySetResult(new ReceiveOutcome(args.SocketError, args.BytesTransferred, null));
                    }
                    finally
                    {
                        if (!pending)
                            args.Dispose();
                    }
                }
                else
                {
                    socket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, result =>
                    {
                        try
                        {
                            int bytes = socket.EndReceive(result, out SocketError error);
                            done.TrySetResult(new ReceiveOutcome(error, bytes, null));
                        }
                        catch (Exception e)
                        {
                            done.TrySetResult(ReceiveOutcome.From(e));
                        }
                    }, null);
                }
            }
            catch (Exception e)
            {
                done.TrySetResult(ReceiveOutcome.From(e));
            }

            return done.Task;
        }

        private sealed class CloseOutcome
        {
            internal bool EndedOnClose;
            internal string OnClose;
            internal bool HandleReused;
            internal bool NewSocketReceived;
            internal string OnNewReceive;
        }

        private sealed class ReceiveOutcome
        {
            internal ReceiveOutcome(SocketError error, int bytes, string exception)
            {
                Error = error;
                Bytes = bytes;
                Exception = exception;
            }

            internal SocketError Error { get; }
            internal int Bytes { get; }
            internal string Exception { get; }

            internal static ReceiveOutcome From(Exception e) =>
                new ReceiveOutcome(e is SocketException se ? se.SocketErrorCode : SocketError.SocketError, 0, e.GetType().Name);

            public override string ToString() =>
                Exception == null ? $"{Error}/{Bytes}B" : $"{Error}/{Exception}";
        }
    }
}
