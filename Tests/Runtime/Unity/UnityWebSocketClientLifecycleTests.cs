using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    /// #91 (pause/resume overlap: a slow close never disposes the newer client; OnDisconnected once per
    /// connection) and #177 (Disable then Destroy in consecutive frames).
    /// </summary>
    public class UnityWebSocketClientLifecycleTests
    {
        private const int MaxFrames = 300;

        private GameObject _go;
        private UnityWebSocketClient _component;
        private List<ScriptedWebSocketClient> _clients;
        private int _connected;
        private List<int> _disconnectCodes;
        private bool _holdNextConnect;

        [SetUp]
        public void SetUp()
        {
            _ = MainThreadDispatcher.Instance;
            _clients = new List<ScriptedWebSocketClient>();
            _disconnectCodes = new List<int>();
            _connected = 0;
            _holdNextConnect = false;

            _go = new GameObject("ws-client-lifecycle");
            _component = _go.AddComponent<UnityWebSocketClient>();
            _component.ConfiguredUri = "ws://127.0.0.1:9/lifecycle";
            _component.ClientFactory = options =>
            {
                var client = new ScriptedWebSocketClient();
                if (_holdNextConnect)
                {
                    _holdNextConnect = false;
                    client.HoldConnect();
                }

                lock (_clients)
                {
                    _clients.Add(client);
                }

                return client;
            };
            _component.OnConnectedEvent.AddListener(() => _connected++);
            _component.OnDisconnectedEvent.AddListener(code => _disconnectCodes.Add(code));
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        [UnityTest]
        public IEnumerator SlowClose_ThenConnect_NewClientIsNeverDisposed_AndDisconnectRaisedOnce()
        {
            var connect1 = _component.ConnectAsync();
            yield return WaitFor(() => connect1.IsCompleted);
            Assert.IsTrue(connect1.Status == TaskStatus.RanToCompletion, "first connect");
            var first = ClientAt(0);
            Assert.IsTrue(_component.IsConnected);

            // Pause: slow closing handshake. Resume: connect while the close is still in flight.
            first.HoldClose();
            var disconnect = _component.DisconnectAsync();
            var connect2 = _component.ConnectAsync();

            yield return null;
            yield return null;
            Assert.AreEqual(1, ClientCount(), "The resume connect waits for the pending close.");
            Assert.IsFalse(first.IsDisposed, "The closing client is not torn down by the connect.");

            first.ReleaseClose();
            yield return WaitFor(() => disconnect.IsCompleted && connect2.IsCompleted);
            Assert.IsTrue(connect2.Status == TaskStatus.RanToCompletion, "second connect");

            yield return WaitFor(() => _disconnectCodes.Count >= 1 && _connected >= 2);
            yield return null;
            yield return null;

            var second = ClientAt(1);
            Assert.IsTrue(first.IsDisposed, "The old client is disposed by its own disconnect.");
            Assert.IsFalse(second.IsDisposed, "The newer client is never disposed by the older disconnect.");
            Assert.AreEqual(0, second.CloseCalls);
            Assert.IsTrue(_component.IsConnected, "The component stays connected on the new client.");
            Assert.AreEqual(2, _connected);
            CollectionAssert.AreEqual(new[] { (int)WebSocketCloseCode.GoingAway }, _disconnectCodes,
                "OnDisconnected is raised exactly once, for the first connection.");
        }

        [UnityTest]
        public IEnumerator DisconnectTwice_SharesOneClose_AndRaisesDisconnectOnce()
        {
            var connect = _component.ConnectAsync();
            yield return WaitFor(() => connect.IsCompleted);
            var first = ClientAt(0);

            first.HoldClose();
            var d1 = _component.DisconnectAsync();
            var d2 = _component.DisconnectAsync();
            first.ReleaseClose();
            yield return WaitFor(() => d1.IsCompleted && d2.IsCompleted);
            yield return WaitFor(() => _disconnectCodes.Count >= 1);
            yield return null;
            yield return null;

            Assert.AreEqual(1, first.CloseCalls);
            Assert.AreEqual(1, _disconnectCodes.Count);
            Assert.IsFalse(_component.IsConnected);
        }

        [UnityTest]
        public IEnumerator FailedClose_RaisesDisconnectOnce_WithAbnormalClosure()
        {
            var connect = _component.ConnectAsync();
            yield return WaitFor(() => connect.IsCompleted);
            var first = ClientAt(0);
            first.FailClose = true;

            LogAssert.Expect(LogType.Error, new Regex("WebSocket disconnect failed"));
            var disconnect = _component.DisconnectAsync();
            yield return WaitFor(() => disconnect.IsCompleted && _disconnectCodes.Count >= 1);
            yield return null;
            yield return null;

            Assert.AreEqual(1, first.AbortCalls, "A failed close aborts the client.");
            CollectionAssert.AreEqual(new[] { (int)WebSocketCloseCode.AbnormalClosure }, _disconnectCodes);
        }

        [UnityTest]
        public IEnumerator DisconnectDuringConnect_SupersedesTheConnect()
        {
            _holdNextConnect = true;
            var connect = _component.ConnectAsync();
            var client = ClientAt(0);
            Assert.AreEqual(WebSocketState.Connecting, client.State);

            _component.Disconnect();
            client.ReleaseConnect();
            yield return WaitFor(() => connect.IsCompleted);

            Assert.IsTrue(connect.IsCanceled, "The superseded connect reports cancellation, not a failure.");
            Assert.IsFalse(_component.IsConnected, "A connect superseded by Disconnect does not publish its client.");
            yield return WaitFor(() => client.IsDisposed && _disconnectCodes.Count >= 1);
            yield return null;
            yield return null;

            Assert.AreEqual(1, client.CloseCalls, "The handshake that completed late is closed gracefully.");
            Assert.AreEqual(1, _connected);
            Assert.AreEqual(1, _disconnectCodes.Count, "Its OnDisconnected is raised once.");
        }

        [UnityTest]
        public IEnumerator DisableThenDestroyInConsecutiveFrames_RaisesNoError_AndClosesTheSocket()
        {
            var connect = _component.ConnectAsync();
            yield return WaitFor(() => connect.IsCompleted);
            var first = ClientAt(0);

            first.HoldClose();
            _go.SetActive(false);
            Assert.AreEqual(1, first.CloseCalls, "OnDisable starts a graceful close.");

            yield return null;
            Object.Destroy(_go);
            _go = null;

            yield return WaitFor(() => first.IsDisposed && first.CloseToken.IsCancellationRequested);
            yield return null;
            yield return null;

            Assert.IsTrue(first.CloseToken.IsCancellationRequested, "OnDestroy cancels the pending close.");
            Assert.GreaterOrEqual(first.AbortCalls, 1, "The socket is aborted.");
            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(0, _disconnectCodes.Count, "No UnityEvent is raised after OnDestroy.");
            LogAssert.NoUnexpectedReceived();
        }

        private ScriptedWebSocketClient ClientAt(int index)
        {
            lock (_clients)
            {
                return _clients[index];
            }
        }

        private int ClientCount()
        {
            lock (_clients)
            {
                return _clients.Count;
            }
        }

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            for (int frame = 0; frame < MaxFrames && !condition(); frame++)
                yield return null;

            Assert.IsTrue(condition(), "Condition not reached within " + MaxFrames + " frames.");
        }
    }
}
