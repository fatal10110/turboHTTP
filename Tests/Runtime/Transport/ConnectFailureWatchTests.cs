using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// A refused connect is reported as a refusal, not as the connect deadline, even when the
    /// runtime's I/O selector never reports it (#1259): IL2CPP's Windows selector drops every socket
    /// past the 64th from its <c>select</c> sets, and Windows reports a failed connect only in the
    /// exception set, so with 64 or more sockets registered the connect task never ended.
    /// </summary>
    [TestFixture]
    public class ConnectFailureWatchTests
    {
        // Far above the ~2 s a refused loopback connect takes on Windows (SYN retries, #226).
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        // More sockets with a pending receive than IL2CPP's Windows select sets hold (FD_SETSIZE 64).
        private const int RegisteredSockets = 72;

        [Test]
        public void IsNeeded_OnlyOnWindows()
        {
            bool windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            Assert.AreEqual(windows, ConnectFailureWatch.IsNeeded);
        }

        [Test]
        public void RefusedConnect_SelectorNeverReports_ThrowsConnectionRefused()
        {
            // The connect task stands in for the selector that lost the refusal: it never ends.
            // Without the watch the wait ran into the deadline (OperationCanceledException).
            if (!ConnectFailureWatch.IsNeeded)
                return; // Only Windows hides a refused connect from a capped selector.

            using var closedPort = new RefusingLoopbackPort();
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Blocking = false;
            try
            {
                socket.Connect(new IPEndPoint(IPAddress.Loopback, closedPort.Port));
                Assert.Fail("a connect to a port that does not listen must not succeed");
            }
            catch (SocketException e) when (SocketErrorNormalizer.Normalize(e) == SocketError.WouldBlock)
            {
                // The non-blocking connect is in progress.
            }

            var lostConnect = new TaskCompletionSource<bool>();
            using var deadline = new CancellationTokenSource(Deadline);
            try
            {
                var ex = AssertAsync.ThrowsAsync<SocketException>(() =>
                    HappyEyeballsConnector.WaitForConnectAsync(socket, lostConnect.Task, deadline.Token));

                // IL2CPP players may report the raw errno (ECONNREFUSED 111 on Android, #1290).
                Assert.AreEqual(SocketError.ConnectionRefused, SocketErrorNormalizer.Normalize(ex));
                Assert.IsFalse(deadline.IsCancellationRequested, "the refusal is reported before the deadline");
            }
            finally
            {
                // Settles the abandoned stand-in so it does not hold back later sockets (#369).
                lostConnect.TrySetResult(true);
            }
        }

        [Test]
        public void RefusedConnect_WithManySocketsRegistered_ThrowsConnectionRefused()
        {
            // The condition of the full Win64 IL2CPP runs: before the watch this connect hung until
            // its deadline there (TimeoutException); elsewhere the selector reports it either way.
            AssertAsync.Run(async () =>
            {
                using var closedPort = new RefusingLoopbackPort();
                using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(RegisteredSockets);

                var clients = new List<Socket>();
                var accepted = new List<Socket>();
                var receives = new List<Task<int>>();
                try
                {
                    for (int i = 0; i < RegisteredSockets; i++)
                    {
                        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        clients.Add(client);
                        client.Connect(listener.LocalEndPoint);
                        accepted.Add(listener.Accept());
                        receives.Add(client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), SocketFlags.None));
                    }

                    Exception failure = null;
                    try
                    {
                        using var refused = await HappyEyeballsConnector.ConnectAsync(
                            new[] { IPAddress.Loopback },
                            closedPort.Port,
                            Deadline,
                            new HappyEyeballsOptions(),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }

                    if (failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                        failure = aggregate.InnerExceptions[0];

                    Assert.IsInstanceOf<SocketException>(failure, "a refused connect is not reported as the deadline: " + failure);
                    // IL2CPP players may report the raw errno (ECONNREFUSED 111 on Android, #1290, #1296).
                    Assert.AreEqual(SocketError.ConnectionRefused, SocketErrorNormalizer.Normalize((SocketException)failure));
                }
                finally
                {
                    // End every pending receive before the sockets close, so no job stays in the
                    // selector after this test (#369).
                    foreach (var peer in accepted)
                    {
                        try { peer.Send(new byte[] { 1 }); } catch (SocketException) { }
                    }

                    var allReceived = Task.WhenAll(receives);
                    await Task.WhenAny(allReceived, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
                    foreach (var receive in receives)
                        _ = receive.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

                    foreach (var socket in clients)
                        socket.Dispose();
                    foreach (var socket in accepted)
                        socket.Dispose();
                }
            });
        }
    }
}
