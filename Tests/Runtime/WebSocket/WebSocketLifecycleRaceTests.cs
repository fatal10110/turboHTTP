using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #84: sends racing an abort, a pong timeout, a dispose or a reconnect surface WebSocketException or
    /// OperationCanceledException, never NullReference, ObjectDisposed (from the writer or the send lock)
    /// or SemaphoreFull.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketLifecycleRaceTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/lifecycle-race");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        private const int Iterations = 25;
        private const int Senders = 6;

        [Test]
        public void ConcurrentSends_RacingAbort_SurfaceOnlyWebSocketOrCancellationErrors()
        {
            AssertAsync.Run(async () =>
            {
                for (int iteration = 0; iteration < Iterations; iteration++)
                {
                    using var peer = new InMemoryWebSocketPeer();
                    var connection = new WebSocketConnection();
                    try
                    {
                        await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                            .ConfigureAwait(false);

                        var errors = new List<Exception>();
                        var senders = StartSenders(connection, errors);

                        await Task.Yield();
                        connection.Abort();

                        await WebSocketReceiveBackpressureTests.WaitWithTimeout(Task.WhenAll(senders), Guard,
                            "Senders did not finish after the abort.").ConfigureAwait(false);

                        // A send that starts after the abort fails the same way.
                        errors.Add(await CaptureAsync(() => connection.SendTextAsync("late", CancellationToken.None))
                            .ConfigureAwait(false));

                        AssertOnlyExpected(errors, allowStateErrors: false);
                        Assert.AreEqual(WebSocketState.Closed, connection.State);
                    }
                    finally
                    {
                        connection.Dispose();
                    }
                }
            });
        }

        [Test]
        public void ConcurrentSends_RacingPongTimeout_SurfaceOnlyWebSocketOrCancellationErrors()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));
                var options = CreateOptions();
                options.PingInterval = TimeSpan.FromMilliseconds(30);
                options.PongTimeout = TimeSpan.FromMilliseconds(30);

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    // The peer stops receiving, so the next ping gets no pong. Sends count as activity and
                    // would postpone the ping, so the senders start once it went out, inside the pong wait,
                    // and the keep-alive loop closes the connection under them.
                    clientStream.DropWrites();
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(clientStream.FirstDroppedWrite, Guard,
                        "The keep-alive ping was not sent.").ConfigureAwait(false);

                    var errors = new List<Exception>();
                    var senders = StartSenders(connection, errors, untilClosed: true);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(Task.WhenAll(senders), Guard,
                        "The pong timeout did not close the connection.").ConfigureAwait(false);

                    AssertOnlyExpected(errors, allowStateErrors: false);
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal);
                    Assert.AreEqual(WebSocketError.PongTimeout, terminal.Error);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void ConcurrentSends_RacingDispose_NeverSurfaceWriterOrLockFailures()
        {
            AssertAsync.Run(async () =>
            {
                for (int iteration = 0; iteration < Iterations; iteration++)
                {
                    using var peer = new InMemoryWebSocketPeer();
                    var connection = new WebSocketConnection();
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    var errors = new List<Exception>();
                    var senders = StartSenders(connection, errors);

                    await Task.Yield();
                    connection.Dispose();

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(Task.WhenAll(senders), Guard,
                        "Senders did not finish after Dispose.").ConfigureAwait(false);

                    // A send issued after Dispose returned is a use-after-dispose of the connection itself;
                    // nothing else may report ObjectDisposed (the writer's RNG, the send semaphore).
                    lock (errors)
                    {
                        foreach (var error in errors)
                        {
                            if (error is ObjectDisposedException disposed)
                            {
                                Assert.AreEqual(nameof(WebSocketConnection), disposed.ObjectName, error.ToString());
                                continue;
                            }

                            Assert.IsTrue(
                                error is WebSocketException || error is OperationCanceledException,
                                "Unexpected " + error);
                        }
                    }
                }
            });
        }

        [Test]
        public void ResilientClient_SendsDuringReconnect_NeverSurfaceObjectDisposedOrNullReference()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new ResilientWebSocketClient(transport);

                var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnReconnected += () => reconnected.TrySetResult(true);

                var options = CreateOptions();
                options.WithReconnection(new WebSocketReconnectPolicy(
                    maxRetries: 3,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    maxDelay: TimeSpan.FromMilliseconds(5),
                    backoffMultiplier: 1.0,
                    jitterFactor: 0.0));

                await client.ConnectAsync(TestUri, options, CancellationToken.None).ConfigureAwait(false);

                var errors = new List<Exception>();
                using var stop = new CancellationTokenSource();
                var senders = new Task[Senders];
                for (int i = 0; i < senders.Length; i++)
                {
                    senders[i] = Task.Run(async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var error = await CaptureAsync(() => client.SendAsync("tick", CancellationToken.None))
                                .ConfigureAwait(false);
                            if (error != null)
                            {
                                lock (errors)
                                    errors.Add(error);
                            }

                            await Task.Yield();
                        }
                    });
                }

                // 1001 Going Away is a reconnect close code: the child client is closed and replaced
                // while the senders keep using whichever child they read.
                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE9 }, fin: true)
                    .ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnected.Task, Guard,
                    "The resilient client did not reconnect.").ConfigureAwait(false);

                stop.Cancel();
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(Task.WhenAll(senders), Guard,
                    "Senders did not stop.").ConfigureAwait(false);

                // Between children the wrapper reports its documented state error (not Open).
                AssertOnlyExpected(errors, allowStateErrors: true);
            });
        }

        private static Task[] StartSenders(WebSocketConnection connection, List<Exception> errors, bool untilClosed = false)
        {
            var senders = new Task[Senders];
            for (int i = 0; i < senders.Length; i++)
            {
                int senderIndex = i;
                senders[i] = Task.Run(async () =>
                {
                    // Small frames, fragmented messages and binary sends exercise the frame and message locks.
                    var large = new byte[4 * 1024];
                    for (int message = 0; untilClosed || message < 20; message++)
                    {
                        Exception error;
                        if ((message + senderIndex) % 3 == 0)
                            error = await CaptureAsync(() => connection.SendBinaryAsync(large, CancellationToken.None))
                                .ConfigureAwait(false);
                        else
                            error = await CaptureAsync(() => connection.SendTextAsync("m" + message, CancellationToken.None))
                                .ConfigureAwait(false);

                        if (error != null)
                        {
                            lock (errors)
                                errors.Add(error);
                            return;
                        }

                        await Task.Yield();
                    }
                });
            }

            return senders;
        }

        private static async Task<Exception> CaptureAsync(Func<Task> send)
        {
            try
            {
                await send().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static void AssertOnlyExpected(List<Exception> errors, bool allowStateErrors)
        {
            lock (errors)
            {
                foreach (var error in errors)
                {
                    if (error == null)
                        continue;

                    bool expected = error is WebSocketException ||
                        error is OperationCanceledException ||
                        (allowStateErrors && error.GetType() == typeof(InvalidOperationException));
                    Assert.IsTrue(expected, "Unexpected exception escaped the public API: " + error);
                }
            }
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 64);
            options.FragmentationThreshold = 512;
            options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500);
            return options;
        }

        private sealed class PeerPerConnectTransport : IWebSocketTransport
        {
            private readonly object _gate = new object();
            private readonly List<InMemoryWebSocketPeer> _peers = new List<InMemoryWebSocketPeer>();

            public InMemoryWebSocketPeer Peer(int index)
            {
                lock (_gate)
                    return _peers[index];
            }

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                var peer = new InMemoryWebSocketPeer();
                lock (_gate)
                    _peers.Add(peer);
                return peer.ConnectAsync(uri, options, ct);
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    foreach (var peer in _peers)
                        peer.Dispose();
                    _peers.Clear();
                }
            }
        }
    }
}
