using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core.Internal;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1116: a throwing user handler (connection events, client events, resilient client events) cannot
    /// skip finalization, fail a completed connect, fail a send or receive, or stop the reconnect loop.
    /// The exception is reported to <see cref="TurboDiagnostics"/> and swallowed.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketHandlerIsolationTests
    {
        private static readonly Uri TestUri = new Uri("ws://localhost/handler-isolation");

        // Guard only: every await finishes long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        private static readonly MethodInfo HandleHealthQualityChangedMethod =
            typeof(WebSocketConnection).GetMethod("HandleHealthQualityChanged", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<string> _diagnostics = new List<string>();

        [SetUp]
        public void SetUp()
        {
            lock (_diagnostics)
                _diagnostics.Clear();
            TurboDiagnostics.SetSink(message =>
            {
                lock (_diagnostics)
                    _diagnostics.Add(message);
            });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.ResetSink();
        }

        [TestCase("Error")]
        [TestCase("StateChanged")]
        public void ThrowingConnectionHandler_NetworkDrop_StillFinalizes(string handlerName)
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    // Set by the handler, which runs last in the finalization: once it is set, the teardown is done.
                    var raisedOnce = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int raised = 0;
                    if (handlerName == "Error")
                    {
                        connection.Error += (_, __) =>
                        {
                            Interlocked.Increment(ref raised);
                            raisedOnce.TrySetResult(true);
                            throw new InvalidOperationException("handler failure");
                        };
                    }
                    else
                    {
                        connection.StateChanged += (_, state) =>
                        {
                            if (state != WebSocketState.Closed)
                                return;
                            Interlocked.Increment(ref raised);
                            raisedOnce.TrySetResult(true);
                            throw new InvalidOperationException("handler failure");
                        };
                    }

                    var receive = connection.ReceiveAsync(CancellationToken.None).AsTask();
                    peer.CloseTransport();

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                            receive.ContinueWith(_ => { }, TaskScheduler.Default), Guard,
                            "ReceiveAsync did not complete after the drop.")
                        .ConfigureAwait(false);

                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(raisedOnce.Task, Guard,
                        handlerName + " was not raised after the drop.").ConfigureAwait(false);

                    Assert.IsTrue(receive.IsFaulted || receive.IsCanceled, "The drop must end the receive with an error.");
                    Assert.AreEqual(1, Volatile.Read(ref raised), handlerName + " must be raised once.");
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                    Assert.IsTrue(clientStream.IsDisposed, "The stream must be disposed.");
                    AssertReported(handlerName);
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void ThrowingMetricsAndQualityHandlers_DoNotFailSendOrReceive()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var options = CreateOptions();
                options.MetricsUpdateMessageInterval = 1;
                options.EnableHealthMonitoring = true;

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    int metricsRaised = 0;
                    int qualityRaised = 0;
                    connection.MetricsUpdated += (_, __) =>
                    {
                        Interlocked.Increment(ref metricsRaised);
                        throw new InvalidOperationException("handler failure");
                    };
                    connection.ConnectionQualityChanged += (_, __) =>
                    {
                        Interlocked.Increment(ref qualityRaised);
                        throw new InvalidOperationException("handler failure");
                    };

                    await connection.SendTextAsync("out", CancellationToken.None).ConfigureAwait(false);

                    await peer.SendTextAsync("in").ConfigureAwait(false);
                    var receive = connection.ReceiveAsync(CancellationToken.None).AsTask();
                    await WebSocketReceiveBackpressureTests.WaitWithTimeout(receive, Guard,
                        "No message arrived.").ConfigureAwait(false);
                    using (var message = receive.Result)
                        Assert.AreEqual("in", message.Text);

                    // The health monitor's quality change, raised the way the monitor raises it.
                    Assert.IsNotNull(HandleHealthQualityChangedMethod);
                    HandleHealthQualityChangedMethod.Invoke(connection, new object[] { ConnectionQuality.Poor });

                    Assert.GreaterOrEqual(Volatile.Read(ref metricsRaised), 1);
                    Assert.AreEqual(1, Volatile.Read(ref qualityRaised));
                    Assert.AreEqual(WebSocketState.Open, connection.State);
                    AssertReported("MetricsUpdated");
                    AssertReported("ConnectionQualityChanged");
                }
                finally
                {
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void WebSocketClient_ThrowingHandlers_ConnectSucceedsAndDropFinalizes()
        {
            AssertAsync.Run(async () =>
            {
                ControlledClientStream clientStream = null;
                using var peer = new InMemoryWebSocketPeer(inner => clientStream = new ControlledClientStream(inner));
                var options = CreateOptions();
                options.MetricsUpdateMessageInterval = 1;
                await using var client = new WebSocketClient(peer);

                // OnClosed is raised last in the finalization: once it is set, the teardown is done.
                var closedOnce = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int connected = 0, errors = 0, closed = 0, metrics = 0;
                client.OnConnected += () =>
                {
                    Interlocked.Increment(ref connected);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnError += _ =>
                {
                    Interlocked.Increment(ref errors);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnClosed += (_, __) =>
                {
                    Interlocked.Increment(ref closed);
                    closedOnce.TrySetResult(true);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnMetricsUpdated += _ =>
                {
                    Interlocked.Increment(ref metrics);
                    throw new InvalidOperationException("handler failure");
                };

                await client.ConnectAsync(TestUri, options, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(WebSocketState.Open, client.State, "A throwing OnConnected must not fail the connect.");

                await client.SendAsync("out", CancellationToken.None).ConfigureAwait(false);
                Assert.GreaterOrEqual(Volatile.Read(ref metrics), 1);

                var receive = client.ReceiveAsync(CancellationToken.None).AsTask();
                peer.CloseTransport();

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(
                        receive.ContinueWith(_ => { }, TaskScheduler.Default), Guard,
                        "ReceiveAsync did not complete after the drop.")
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(closedOnce.Task, Guard,
                    "OnClosed was not raised after the drop.").ConfigureAwait(false);

                Assert.IsTrue(receive.IsFaulted || receive.IsCanceled, "The drop must end the receive with an error.");
                Assert.AreEqual(1, Volatile.Read(ref connected));
                Assert.GreaterOrEqual(Volatile.Read(ref errors), 1);
                Assert.AreEqual(1, Volatile.Read(ref closed));
                Assert.AreEqual(WebSocketState.Closed, client.State);
                Assert.IsTrue(clientStream.IsDisposed, "The stream must be disposed.");
                AssertReported("OnConnected");
                AssertReported("OnError");
                AssertReported("OnClosed");
                AssertReported("OnMetricsUpdated");
            });
        }

        [Test]
        public void ResilientClient_ThrowingHandlers_ReconnectLoopContinues()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new PeerPerConnectTransport();
                await using var client = new ResilientWebSocketClient(transport);

                var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int connected = 0, errors = 0, reconnecting = 0;
                client.OnConnected += () =>
                {
                    Interlocked.Increment(ref connected);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnError += _ =>
                {
                    Interlocked.Increment(ref errors);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnReconnecting += (_, __) =>
                {
                    Interlocked.Increment(ref reconnecting);
                    throw new InvalidOperationException("handler failure");
                };
                client.OnReconnected += () =>
                {
                    reconnected.TrySetResult(true);
                    throw new InvalidOperationException("handler failure");
                };

                await client.ConnectAsync(TestUri, CreateResilientOptions(), CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(WebSocketState.Open, client.State, "A throwing OnConnected must not fail the connect.");

                var receive = client.ReceiveAsync(CancellationToken.None).AsTask();

                // 1001 Going Away is a reconnect close code: the loop raises OnError and OnReconnecting, both
                // throw, and it still connects the replacement (peer 1).
                await transport.Peer(0).SendFrameAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE9 }, fin: true)
                    .ConfigureAwait(false);

                await WebSocketReceiveBackpressureTests.WaitWithTimeout(reconnected.Task, Guard,
                    "A throwing handler stopped the reconnect loop.").ConfigureAwait(false);

                // The pull consumer waiting across the reconnect gets the replacement's message.
                await transport.Peer(1).SendTextAsync("after-reconnect").ConfigureAwait(false);
                await WebSocketReceiveBackpressureTests.WaitWithTimeout(receive, Guard,
                    "The pull consumer was left hanging after the reconnect.").ConfigureAwait(false);
                using (var message = receive.Result)
                    Assert.AreEqual("after-reconnect", message.Text);

                Assert.AreEqual(1, Volatile.Read(ref connected));
                Assert.GreaterOrEqual(Volatile.Read(ref errors), 1);
                Assert.AreEqual(1, Volatile.Read(ref reconnecting));
                AssertReported("OnReconnecting");
                AssertReported("OnReconnected");
            });
        }

        private void AssertReported(string eventName)
        {
            string expected = "WebSocket " + eventName + " handler threw.";
            lock (_diagnostics)
            {
                foreach (var message in _diagnostics)
                {
                    if (message.Contains(expected))
                    {
                        StringAssert.DoesNotContain("handler failure", message,
                            "Diagnostics carry the exception type, never its message.");
                        return;
                    }
                }

                Assert.Fail("No diagnostic for the throwing " + eventName + " handler. Logged: " +
                    string.Join(" | ", _diagnostics));
            }
        }

        private static WebSocketConnectionOptions CreateOptions()
        {
            return WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8);
        }

        private static WebSocketConnectionOptions CreateResilientOptions()
        {
            var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 32);
            options.CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500);
            return options.WithReconnection(new WebSocketReconnectPolicy(
                maxRetries: 3,
                initialDelay: TimeSpan.FromMilliseconds(1),
                maxDelay: TimeSpan.FromMilliseconds(5),
                backoffMultiplier: 1.0,
                jitterFactor: 0.0));
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
