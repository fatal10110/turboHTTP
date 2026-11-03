using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Unity;
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #177: Unity never disposes a stopped coroutine's enumerator, so the handle returned by
    /// ReceiveAsCoroutine must release the lifecycle binding and the pending receive itself.
    /// </summary>
    public class UnityWebSocketReceiveCoroutineTests
    {
        private const int MaxFrames = 300;

        [UnityTest]
        public IEnumerator StopCoroutine_ThenDispose_CancelsPendingReceive()
        {
            _ = MainThreadDispatcher.Instance;
            var go = new GameObject("ws-coroutine-host");
            var host = go.AddComponent<WebSocketCoroutineHost>();
            var fake = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(fake);
            try
            {
                var received = new List<string>();
                var handle = bridge.ReceiveAsCoroutine(m => received.Add(m.Text), callbackOwner: host);
                var running = host.StartCoroutine(handle);

                yield return null;
                Assert.IsTrue(fake.CompleteReceive(ScriptedWebSocketClient.CreateTextMessage("one")));
                for (int frame = 0; frame < MaxFrames && received.Count < 1; frame++)
                    yield return null;

                CollectionAssert.AreEqual(new[] { "one" }, received);
                var pendingToken = fake.ReceiveToken;
                Assert.IsTrue(pendingToken.CanBeCanceled);
                Assert.IsFalse(pendingToken.IsCancellationRequested, "The next receive is pending.");

                host.StopCoroutine(running);
                yield return null;
                Assert.IsFalse(pendingToken.IsCancellationRequested, "Unity does not dispose a stopped coroutine.");

                handle.Dispose();

                Assert.IsTrue(handle.IsDisposed);
                Assert.IsTrue(pendingToken.IsCancellationRequested, "Dispose cancels the abandoned receive.");
                Assert.IsFalse(handle.MoveNext());
            }
            finally
            {
                bridge.Dispose();
                Object.DestroyImmediate(go);
            }
        }

        [UnityTest]
        public IEnumerator DestroyingTheOwner_CancelsPendingReceive_WithoutDispose()
        {
            _ = MainThreadDispatcher.Instance;
            var go = new GameObject("ws-coroutine-owner");
            var host = go.AddComponent<WebSocketCoroutineHost>();
            var fake = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(fake);
            try
            {
                var handle = bridge.ReceiveAsCoroutine(m => m.Dispose(), callbackOwner: host);
                host.StartCoroutine(handle);

                yield return null;
                var pendingToken = fake.ReceiveToken;
                Assert.IsFalse(pendingToken.IsCancellationRequested);

                Object.Destroy(go);
                go = null;
                for (int frame = 0; frame < MaxFrames && !pendingToken.IsCancellationRequested; frame++)
                    yield return null;

                Assert.IsTrue(pendingToken.IsCancellationRequested, "Owner destruction cancels the pending receive.");
                handle.Dispose();
            }
            finally
            {
                bridge.Dispose();
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispose_BeforeFirstMoveNext_IsSafe_AndEndsTheCoroutine()
        {
            var fake = new ScriptedWebSocketClient();
            var bridge = new UnityWebSocketBridge(fake);
            try
            {
                var handle = bridge.ReceiveAsCoroutine(m => m.Dispose());
                handle.Dispose();
                handle.Dispose();

                Assert.IsFalse(handle.MoveNext());
                Assert.IsTrue(handle.IsDisposed);
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}
