using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Unity;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #90: backpressure drops are detected by type (not by message text), counted through OnMessageDropped
    /// without an exception log per drop, and lifecycle callbacks are never dropped.
    /// Events are emitted from the main thread with inline execution off, so every enqueue happens before the
    /// dispatcher drains anything: the saturation is deterministic.
    /// </summary>
    public class UnityWebSocketBridgeDispatchTests
    {
        private const int MaxFrames = 300;

        [UnityTest]
        public IEnumerator Reject_DroppedMessagesRaiseOnMessageDropped_AndOnClosedIsStillDelivered()
        {
            _ = MainThreadDispatcher.Instance;
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(Saturating(MainThreadBackpressurePolicy.Reject, capacity: 1));

            var fake = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(fake);
            try
            {
                int delivered = 0;
                int dropped = 0;
                int closed = 0;
                int closedThread = -1;
                WebSocketCloseCode closedCode = default;
                int mainThread = Thread.CurrentThread.ManagedThreadId;

                bridge.OnMessage += m =>
                {
                    delivered++;
                    m.Dispose();
                };
                bridge.OnMessageDropped += () => Interlocked.Increment(ref dropped);
                bridge.OnClosed += (code, reason) =>
                {
                    closed++;
                    closedCode = code;
                    closedThread = Thread.CurrentThread.ManagedThreadId;
                };

                for (int i = 0; i < 8; i++)
                    fake.EmitMessage("m" + i);

                fake.EmitClosed(WebSocketCloseCode.NormalClosure, "bye");

                for (int frame = 0; frame < MaxFrames && (delivered < 1 || closed < 1); frame++)
                    yield return null;

                yield return null;

                Assert.AreEqual(7, Volatile.Read(ref dropped), "The 7 rejected messages raise OnMessageDropped.");
                Assert.AreEqual(1, delivered, "Only the queued message is delivered.");
                Assert.AreEqual(1, closed, "OnClosed is delivered once although the user queue is full.");
                Assert.AreEqual(WebSocketCloseCode.NormalClosure, closedCode);
                Assert.AreEqual(mainThread, closedThread, "OnClosed runs on the main thread.");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                bridge.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DropOldest_EvictedMessagesRaiseOnMessageDropped_AndEvictedOnClosedIsRedelivered()
        {
            _ = MainThreadDispatcher.Instance;
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(Saturating(MainThreadBackpressurePolicy.DropOldest, capacity: 2));

            var fake = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(fake);
            try
            {
                int delivered = 0;
                int dropped = 0;
                int closed = 0;
                int errors = 0;

                bridge.OnMessage += m =>
                {
                    delivered++;
                    m.Dispose();
                };
                bridge.OnMessageDropped += () => Interlocked.Increment(ref dropped);
                bridge.OnError += e => errors++;
                bridge.OnClosed += (code, reason) => closed++;

                // Queue [m3, m4]: m0, m1, m2 evicted.
                for (int i = 0; i < 5; i++)
                    fake.EmitMessage("m" + i);

                // OnError then OnClosed evict m3, m4: queue [error, closed].
                fake.EmitError("boom");
                fake.EmitClosed(WebSocketCloseCode.GoingAway, "late");

                // Two more messages evict OnError and OnClosed themselves: both move to the control lane.
                fake.EmitMessage("m5");
                fake.EmitMessage("m6");

                for (int frame = 0; frame < MaxFrames && (delivered < 2 || closed < 1 || errors < 1); frame++)
                    yield return null;

                yield return null;
                yield return null;

                Assert.AreEqual(5, Volatile.Read(ref dropped), "m0-m4 are evicted and reported; lifecycle callbacks are not drops.");
                Assert.AreEqual(2, delivered, "m5 and m6 are delivered.");
                Assert.AreEqual(1, errors, "The evicted OnError is re-dispatched exactly once.");
                Assert.AreEqual(1, closed, "The evicted OnClosed is re-dispatched exactly once.");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
                bridge.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DropOldest_EvictedWork_FaultsWithTypedRejection_NotCancellation()
        {
            _ = MainThreadDispatcher.Instance;
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(Saturating(MainThreadBackpressurePolicy.DropOldest, capacity: 1));
            try
            {
                var first = MainThreadDispatcher.ExecuteAsync(() => { });
                var second = MainThreadDispatcher.ExecuteAsync(() => { });

                for (int frame = 0; frame < MaxFrames && !(first.IsCompleted && second.IsCompleted); frame++)
                    yield return null;

                Assert.IsTrue(first.IsFaulted, "The evicted item faults instead of reporting a caller cancellation.");
                var rejection = first.Exception?.GetBaseException() as MainThreadDispatchRejectedException;
                Assert.IsNotNull(rejection);
                Assert.AreEqual(MainThreadDispatchRejectionReason.DroppedOldest, rejection.Reason);
                Assert.IsTrue(second.Status == System.Threading.Tasks.TaskStatus.RanToCompletion);
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }

        [Test]
        public void Reject_QueueFull_ThrowsTypedRejection_ThatIsAnInvalidOperationException()
        {
            var settings = Saturating(MainThreadBackpressurePolicy.Reject, capacity: 1);
            var queue = new MainThreadWorkQueue(settings);
            queue.TryEnqueueImmediate(new NoopWorkItem(), MainThreadDispatcherWorkKind.User, out _);

            Assert.IsFalse(queue.TryEnqueueImmediate(new NoopWorkItem(), MainThreadDispatcherWorkKind.User, out var rejection));
            Assert.IsInstanceOf<MainThreadDispatchRejectedException>(rejection);
            Assert.IsInstanceOf<InvalidOperationException>(rejection);
            Assert.AreEqual(
                MainThreadDispatchRejectionReason.QueueFull,
                ((MainThreadDispatchRejectedException)rejection).Reason);
        }

        private static MainThreadDispatcherSettings Saturating(MainThreadBackpressurePolicy policy, int capacity)
        {
            return new MainThreadDispatcherSettings
            {
                UserQueueCapacity = capacity,
                BackpressurePolicy = policy,
                MaxPendingWaiters = 8,
                WaitTimeout = TimeSpan.FromMilliseconds(10),
                AllowInlineExecutionOnMainThread = false
            };
        }

        private sealed class NoopWorkItem : IMainThreadDispatchWorkItem
        {
            public void Execute()
            {
            }

            public void Fail(Exception exception)
            {
            }
        }
    }
}
