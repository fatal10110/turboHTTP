using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.WebSocket;
using TurboHTTP.Unity;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #78 Unity coverage: the bridge (the delivery path UnityWebSocketClient uses) is an event-only
    /// consumer of a real WebSocketClient and must not stall on the client's bounded pull queue.
    /// </summary>
    public class UnityWebSocketBackpressureTests
    {
        private const int FloodCount = 600;

        [UnityTest]
        public IEnumerator UnityWebSocketBridge_RealClient_EventOnly_ReceivesFloodWithoutStalling()
        {
            _ = MainThreadDispatcher.Instance;

            var peer = new InMemoryWebSocketPeer();
            var client = new WebSocketClient(peer);
            var bridge = new UnityWebSocketBridge(client);

            int received = 0;
            int dropped = 0;
            bridge.OnMessage += message =>
            {
                try
                {
                    Interlocked.Increment(ref received);
                }
                finally
                {
                    message?.Dispose();
                }
            };
            bridge.OnMessageDropped += () => Interlocked.Increment(ref dropped);

            try
            {
                var options = new WebSocketConnectionOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(5),
                    PingInterval = TimeSpan.Zero,
                    IdleTimeout = TimeSpan.Zero,
                    ReceiveQueueCapacity = 4
                };

                var connect = bridge.ConnectAsync(new Uri("ws://localhost/unity-backpressure"), options, CancellationToken.None);
                yield return WaitForTask(connect, 10f, "connect");

                var flood = peer.SendManyTextAsync(FloodCount);
                yield return WaitForTask(flood, 10f, "flood");

                float deadline = Time.realtimeSinceStartup + 15f;
                yield return new WaitUntil(() =>
                    Volatile.Read(ref received) + Volatile.Read(ref dropped) >= FloodCount ||
                    Time.realtimeSinceStartup > deadline);

                Assert.AreEqual(
                    FloodCount,
                    Volatile.Read(ref received) + Volatile.Read(ref dropped),
                    "Event-only bridge stalled; dispatched " + Volatile.Read(ref received) +
                    ", dropped " + Volatile.Read(ref dropped) + ".");
                Assert.AreEqual(WebSocketState.Open, client.State);
            }
            finally
            {
                bridge.Dispose();
                peer.Dispose();
            }
        }

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
